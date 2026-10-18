using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Palanca.Tests.Owners;

/// <summary>
/// A passkey authenticator in memory, for driving registration and sign-in without a browser. It answers WebAuthn
/// options JSON with the credential JSON a browser would post: ES256 keys, "none" attestation, user presence and
/// user verification, and discoverable credentials that return their user handle.
/// </summary>
public sealed class SoftwareAuthenticator : IDisposable
{
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte AttestedCredentialData = 0x40;

    private static readonly string[] Transports = ["internal"];

    private readonly List<Credential> _credentials = [];

    /// <summary>The origin written into client data; a browser reports the page's origin.</summary>
    public string Origin { get; set; } = PalancaFactory.Origin;

    public int CredentialCount => _credentials.Count;

    /// <summary>Whether responses claim the user was verified, by biometrics or a PIN.</summary>
    public bool UserVerifies { get; set; } = true;

    /// <summary>Answers creation options with a new credential, as <c>navigator.credentials.create()</c> would.</summary>
    public string Create(string creationOptionsJson)
    {
        var options = JsonNode.Parse(creationOptionsJson)!;
        var credential = new Credential(
            RandomNumberGenerator.GetBytes(16),
            ECDsa.Create(ECCurve.NamedCurves.nistP256),
            Base64Url.DecodeFromChars(options["user"]!["id"]!.GetValue<string>()),
            options["rp"]!["id"]!.GetValue<string>());
        _credentials.Add(credential);

        var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>());
        var authenticatorData = AuthenticatorData(credential.RelyingPartyId, UserPresent | UserVerified | AttestedCredentialData, 0, credential);
        return JsonSerializer.Serialize(new
        {
            id = Base64Url.EncodeToString(credential.Id),
            rawId = Base64Url.EncodeToString(credential.Id),
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientData),
                attestationObject = Base64Url.EncodeToString(AttestationObject(authenticatorData)),
                transports = Transports,
            },
        });
    }

    /// <summary>Answers request options with an assertion from the newest credential, or the one at <paramref name="index"/>.</summary>
    public string Get(string requestOptionsJson, int? index = null)
    {
        var options = JsonNode.Parse(requestOptionsJson)!;
        var credential = _credentials[index ?? _credentials.Count - 1];
        credential.SignCount++;

        var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>());
        var authenticatorData = AuthenticatorData(options["rpId"]!.GetValue<string>(), UserPresent | (UserVerifies ? UserVerified : 0), credential.SignCount, attested: null);
        var signature = credential.Key.SignData([.. authenticatorData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.Serialize(new
        {
            id = Base64Url.EncodeToString(credential.Id),
            rawId = Base64Url.EncodeToString(credential.Id),
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientData),
                authenticatorData = Base64Url.EncodeToString(authenticatorData),
                signature = Base64Url.EncodeToString(signature),
                userHandle = Base64Url.EncodeToString(credential.UserHandle),
            },
        });
    }

    public void Dispose()
    {
        foreach (var credential in _credentials)
        {
            credential.Key.Dispose();
        }
    }

    private byte[] ClientData(string type, string challenge) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin = Origin, crossOrigin = false }));

    private static byte[] AuthenticatorData(string relyingPartyId, int flags, uint signCount, Credential? attested)
    {
        var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(relyingPartyId)));
        data.Add((byte)flags);
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, signCount);
        data.AddRange(counter);
        if (attested is not null)
        {
            data.AddRange(new byte[16]); // AAGUID: none.
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)attested.Id.Length);
            data.AddRange(length);
            data.AddRange(attested.Id);
            data.AddRange(CosePublicKey(attested.Key));
        }

        return [.. data];
    }

    // CTAP2 canonical order: integer keys ascending by encoding (1, 3, -1, -2, -3), text keys by length.
    private static byte[] CosePublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(2); // kty: EC2
        writer.WriteInt32(3);
        writer.WriteInt32(-7); // alg: ES256
        writer.WriteInt32(-1);
        writer.WriteInt32(1); // crv: P-256
        writer.WriteInt32(-2);
        writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3);
        writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static byte[] AttestationObject(byte[] authenticatorData)
    {
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteTextString("authData");
        writer.WriteByteString(authenticatorData);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private sealed class Credential(byte[] id, ECDsa key, byte[] userHandle, string relyingPartyId)
    {
        public byte[] Id { get; } = id;
        public ECDsa Key { get; } = key;
        public byte[] UserHandle { get; } = userHandle;
        public string RelyingPartyId { get; } = relyingPartyId;
        public uint SignCount { get; set; }
    }
}
