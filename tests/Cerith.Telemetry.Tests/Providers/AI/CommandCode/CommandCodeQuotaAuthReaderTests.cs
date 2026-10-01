#nullable enable

using System;
using Cerith.TelemetryService.Providers.AI.CommandCode;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cerith.Telemetry.Tests.Providers.AI.CommandCode;

[TestClass]
public sealed class CommandCodeQuotaAuthReaderTests
{
    [TestMethod]
    public void NonEmptyEnvironmentKeyWinsWithoutReadingAuthFile()
    {
        var fileReads = 0;
        var reader = new CommandCodeQuotaAuthReader(
            () => "selected-environment-key",
            () => null,
            () => null,
            _ => { fileReads++; return "{\"apiKey\":\"file-key\"}"; });

        var result = reader.Read();

        Assert.AreEqual(CommandCodeAuthStatus.Available, result.Status);
        Assert.AreEqual("environment", result.Credential!.Source);
        Assert.AreEqual("selected-environment-key", result.Credential.ApiKey);
        Assert.AreEqual(0, fileReads);
    }

    [TestMethod]
    public void DifferingHomeVariablesFailClosedBeforeReadingAuthFile()
    {
        var fileReads = 0;
        var reader = new CommandCodeQuotaAuthReader(
            () => null,
            () => "C:\\Users\\first",
            () => "C:\\Users\\second",
            _ => { fileReads++; return "{\"apiKey\":\"file-key\"}"; });

        var result = reader.Read();

        Assert.AreEqual(CommandCodeAuthStatus.AmbiguousHome, result.Status);
        Assert.AreEqual("auth_source_ambiguous", result.ErrorCode);
        Assert.AreEqual(0, fileReads);
    }

    [TestMethod]
    public void RelativeHomeVariablesAreRejectedBeforeReadingAuthFile()
    {
        var fileReads = 0;
        var readers = new[]
        {
            new CommandCodeQuotaAuthReader(() => null, () => "relative-home", () => null,
                _ => { fileReads++; return "{\"apiKey\":\"file-key\"}"; }),
            new CommandCodeQuotaAuthReader(() => null, () => null, () => "relative-profile",
                _ => { fileReads++; return "{\"apiKey\":\"file-key\"}"; })
        };

        foreach (var reader in readers)
        {
            var result = reader.Read();

            Assert.AreEqual(CommandCodeAuthStatus.Invalid, result.Status);
            Assert.AreEqual("auth_invalid", result.ErrorCode);
        }

        Assert.AreEqual(0, fileReads);
    }

    [TestMethod]
    public void CliAuthReadsOnlyApiKeyField()
    {
        var reader = new CommandCodeQuotaAuthReader(
            () => null,
            () => "C:\\Users\\test",
            () => null,
            path =>
            {
                Assert.AreEqual("C:\\Users\\test\\.commandcode\\auth.json", path);
                return "{\"apiKey\":\"file-key\",\"user\":\"private@example.invalid\"}";
            });

        var result = reader.Read();

        Assert.AreEqual(CommandCodeAuthStatus.Available, result.Status);
        Assert.AreEqual("cli-auth-file", result.Credential!.Source);
        Assert.AreEqual("file-key", result.Credential.ApiKey);
        Assert.IsFalse(result.Credential.Fingerprint.Contains("file-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InvalidNonEmptyEnvironmentValueDoesNotFallBackToFile()
    {
        var fileReads = 0;
        var reader = new CommandCodeQuotaAuthReader(
            () => "   ",
            () => "C:\\Users\\test",
            () => null,
            _ => { fileReads++; return "{\"apiKey\":\"file-key\"}"; });

        var result = reader.Read();

        Assert.AreEqual(CommandCodeAuthStatus.Invalid, result.Status);
        Assert.AreEqual(0, fileReads);
    }

    [TestMethod]
    public void OversizedCliAuthFileIsRejectedBeforeParsing()
    {
        const int authFileLimitBytes = 16 * 1024;
        var fileReads = 0;
        var reader = new CommandCodeQuotaAuthReader(
            () => null,
            () => "C:\\Users\\test",
            () => null,
            _ =>
            {
                fileReads++;
                return new string('x', authFileLimitBytes + 1);
            });

        var result = reader.Read();

        Assert.AreEqual(CommandCodeAuthStatus.Invalid, result.Status);
        Assert.AreEqual("auth_invalid", result.ErrorCode);
        Assert.AreEqual(1, fileReads);
    }
}
