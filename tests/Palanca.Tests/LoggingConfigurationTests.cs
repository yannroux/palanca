using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Palanca.Tests;

public sealed class LoggingConfigurationTests
{
    [Fact]
    public async Task ProductionWritesJsonLogsToTheConsole()
    {
        using var directory = new TempDirectory();
        await using var factory = PalancaFactory.InDirectory(directory.Path, environment: "Production");

        var options = factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;

        Assert.Equal(ConsoleFormatterNames.Json, options.FormatterName);
    }
}
