// Palanca's only script: the browser side of a passkey ceremony (FLG-044).
// <passkey-submit> sits in a form next to a submit button named __passkeySubmit. When that button submits the
// form, the element asks the server for WebAuthn options, lets the browser create or use a passkey, and posts
// the form with the credential as <name>.CredentialJson, or with <name>.Error set to a short code.

const supported =
    typeof window.PublicKeyCredential === "function" &&
    typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function" &&
    typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function";

class PasskeySubmit extends HTMLElement {
    static formAssociated = true;

    connectedCallback() {
        this.internals = this.attachInternals();
        const form = this.internals.form;
        if (!form) {
            return;
        }

        form.addEventListener("submit", (event) => {
            if (event.submitter?.name !== "__passkeySubmit") {
                return;
            }

            event.preventDefault();
            this.run(form, event.submitter);
        });
    }

    async run(form, button) {
        if (!form.reportValidity()) {
            return;
        }

        button.disabled = true;
        button.setAttribute("aria-busy", "true");
        const name = this.getAttribute("name");
        const data = new FormData();
        try {
            if (!supported) {
                throw new PasskeyError("unsupported");
            }

            const options = await this.fetchOptions(form);
            const credential = this.getAttribute("operation") === "create"
                ? await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options) })
                : await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options) });
            data.append(`${name}.CredentialJson`, JSON.stringify(credential));
        } catch (error) {
            console.error(error);
            // DOMException has a numeric legacy "code", so only this script's own errors carry a code to post.
            data.append(`${name}.Error`, error instanceof PasskeyError ? error.code : errorCodes[error.name] ?? "browser");
        }

        this.internals.setFormValue(data);
        form.submit();
    }

    async fetchOptions(form) {
        const body = {};
        const tokenField = this.getAttribute("token-field");
        if (tokenField) {
            body.token = new FormData(form).get(tokenField);
        }

        const response = await fetch(this.getAttribute("options-url"), {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                [this.getAttribute("request-token-name")]: this.getAttribute("request-token-value"),
            },
            body: JSON.stringify(body),
        });
        if (!response.ok) {
            throw new PasskeyError(`options-${response.status}`);
        }

        return await response.json();
    }
}

// WebAuthn's error names, as the codes the server words for the page.
const errorCodes = {
    NotAllowedError: "cancelled",
    InvalidStateError: "exists",
};

class PasskeyError extends Error {
    constructor(code) {
        super(code);
        this.code = code;
    }
}

customElements.define("passkey-submit", PasskeySubmit);

// An enrollment link carries its token in the fragment, which browsers never send to the server or in a
// Referer header. Move it into the form, and out of the address bar and history.
const tokenInput = document.querySelector("input[data-enrollment-token]");
if (tokenInput) {
    const token = location.hash.slice(1);
    if (token) {
        tokenInput.value = token;
        history.replaceState(null, "", location.pathname);
    } else if (!tokenInput.value) {
        document.querySelector("[data-enrollment-missing]")?.removeAttribute("hidden");
        document.querySelectorAll("[data-enrollment-form]").forEach((element) => element.setAttribute("hidden", ""));
    }
}
