using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class FinalBuildConsolidationTests
{
    [Test]
    public void NormalBuildAlwaysIncludesReferencePoliciesWithoutFeatureDefines()
    {
        var build = CommandLineAndroidBuild.CreateAndroidBuildOptions("final.apk", BuildOptions.None);
        Assert.That(build.extraScriptingDefines, Is.Empty);
        Assert.That(build.options, Is.EqualTo(BuildOptions.None));
        Assert.That(DjiAprilTagBorderExperiment.Resolve(64, 4, true, DjiAprilTagBorderExperiment.Enabled), Is.EqualTo(48));
        Assert.That(DjiAprilTagBorderExperiment.Resolve(64, 3, true, DjiAprilTagBorderExperiment.Enabled), Is.EqualTo(64));
        Assert.That(DjiAprilTagBorderRetentionExperiment.Resolve(64, 4, 52, DjiRenderPoseSource.Visual,
            true, DjiAprilTagBorderRetentionExperiment.Enabled), Is.EqualTo(48));
        Assert.That(DjiAprilTagNoMarkersQualityLossExperiment.Enabled, Is.True);
        Assert.That(DjiAprilTagStaleJumpExperiment.Enabled, Is.True);
        Assert.That(DjiValidatedTrackingCandidate.IsFreshPositiveSource(100, 2000000101, 0), Is.False);
        Assert.That(DjiValidatedTrackingCandidate.IsRotationConsistent(Pose.identity,
            new Pose(Vector3.zero, Quaternion.AngleAxis(6.1f, Vector3.up)), 6, out _), Is.False);
        Assert.That(new VerticalAltitudeSmoothingEstimator().Mode, Is.EqualTo(VerticalAltitudeSmoothingMode.RawTarget));
        Assert.That(HorizontalGnssSmoothingEstimator.DefaultTauSeconds, Is.EqualTo(.25f));
        Assert.That(HorizontalGnssSmoothingEstimator.ProvisionalStationaryNoiseEnvelopeMeters, Is.EqualTo(.04f));
        Assert.That(RoofCanopyWallShadowExperiment.Enabled, Is.False, "The justified Editor/platform distinction remains.");
    }

    [TestCase(null, 0)]
    [TestCase("DJI_EXACT_DETECTOR_CAPTURE", 1)]
    [TestCase("DJI_RELATIVE_POSITION_EXPERIMENT", 1)]
    public void DiagnosticCapabilityIsExplicitAndCannotChangeFinalTracking(string capability, int count)
    {
        var defines = CommandLineAndroidBuild.DiagnosticDefines(capability);
        Assert.That(defines, Has.Length.EqualTo(count));
        if (capability != null) Assert.That(defines.Single(), Is.EqualTo(capability));
        Assert.That(DjiValidatedTrackingCandidate.Enabled && DjiAprilTagBorderExperiment.Enabled &&
                    DjiAprilTagBorderRetentionExperiment.Enabled && DjiAprilTagStaleJumpExperiment.Enabled &&
                    DjiAprilTagNoMarkersQualityLossExperiment.Enabled, Is.True);
    }

    [Test]
    public void RemovedCandidateSwitchCannotBeRequestedAsADiagnostic()
    {
        Assert.Throws<ArgumentException>(() => CommandLineAndroidBuild.DiagnosticDefines("DJI_VALIDATED_TRACKING_CANDIDATE"));
    }

    [Test]
    public void CurrentAarHasMatchingProvenanceAndActualOnGetter()
    {
        Assert.DoesNotThrow(FinalBridgeArtifactGuard.ValidateCurrentProject);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ElfGetterVerificationReadsActualMachineCode(bool enabled)
    {
        var elf = Elf(enabled);
        if (enabled) Assert.DoesNotThrow(() => FinalBridgeArtifactGuard.ValidateArm64Getter(elf));
        else Assert.Throws<InvalidDataException>(() => FinalBridgeArtifactGuard.ValidateArm64Getter(elf));
    }

    [Test]
    public void TruncatedOrWrongArchitectureCannotPassNativeVerification()
    {
        Assert.Throws<InvalidDataException>(() => FinalBridgeArtifactGuard.ValidateArm64Getter(new byte[40]));
        var wrong = Elf(true);
        Put(wrong, 18, (ushort)62);
        Assert.Throws<InvalidDataException>(() => FinalBridgeArtifactGuard.ValidateArm64Getter(wrong));
    }

    [TestCase("local")]
    [TestCase("data")]
    [TestCase("hidden")]
    [TestCase("nonExecutable")]
    [TestCase("wrongSize")]
    public void NonCallableGetterCannotPassNativeVerification(string defect)
    {
        var elf = Elf(true);
        if (defect == "local") elf[664 + 4] = 2;
        if (defect == "data") elf[664 + 4] = 0x11;
        if (defect == "hidden") elf[664 + 5] = 2;
        if (defect == "nonExecutable") Put(elf, 128 + 8, 2UL);
        if (defect == "wrongSize") Put(elf, 664 + 16, 4UL);
        Assert.Throws<InvalidDataException>(() => FinalBridgeArtifactGuard.ValidateArm64Getter(elf));
    }

    [TestCase(true, true, true)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    public void AarRequiresManifestAndMachineCodeAgreement(bool actualOn, bool declaredOn, bool passes)
    {
        WithAar(actualOn, declaredOn, false, path =>
        {
            if (passes) Assert.DoesNotThrow(() => FinalBridgeArtifactGuard.ValidateAar(path));
            else Assert.Throws<InvalidDataException>(() => FinalBridgeArtifactGuard.ValidateAar(path));
        });
    }

    [Test]
    public void AarRejectsStaleNativeHashEvenWhenFeatureIsDeclaredOn()
    {
        WithAar(true, true, true, path => Assert.Throws<InvalidDataException>(() => FinalBridgeArtifactGuard.ValidateAar(path)));
    }

    [Test]
    public void AarWithoutFeatureManifestIsNotAReleasePlugin()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".aar");
        try
        {
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) z.CreateEntry("unrelated.txt");
            Assert.Throws<InvalidDataException>(() => FinalBridgeArtifactGuard.ValidateAar(path));
        }
        finally { File.Delete(path); }
    }

    private static void WithAar(bool actualOn, bool declaredOn, bool badHash, Action<string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".aar");
        try
        {
            var elf = Elf(actualOn);
            using var hash = SHA256.Create();
            var digest = BitConverter.ToString(hash.ComputeHash(elf)).Replace("-", string.Empty);
            var manifest = new FinalBridgeArtifactGuard.Manifest
            {
                schemaVersion = 1, fourTagMultiStart = declaredOn,
                nativeLibraries = new[] { new FinalBridgeArtifactGuard.NativeLibrary
                { abi = "arm64-v8a", sha256 = badHash ? new string('0', 64) : digest } }
            };
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(z.CreateEntry(FinalBridgeArtifactGuard.ManifestPath).Open()))
                    writer.Write(JsonUtility.ToJson(manifest));
                using var native = z.CreateEntry("jni/arm64-v8a/libdjiunity.so").Open();
                native.Write(elf, 0, elf.Length);
            }
            check(path);
        }
        finally { File.Delete(path); }
    }

    private static byte[] Elf(bool enabled)
    {
        var bytes = new byte[768];
        bytes[0] = 0x7f; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = 2; bytes[5] = 1;
        Put(bytes, 18, (ushort)183); Put(bytes, 40, 64UL); Put(bytes, 58, (ushort)64); Put(bytes, 60, (ushort)4);
        // .text, .dynstr, .dynsym; one exported function, one null symbol.
        Put(bytes, 128 + 4, 1U); Put(bytes, 128 + 8, 6UL);
        Put(bytes, 128 + 16, 0x1000UL); Put(bytes, 128 + 24, 384UL); Put(bytes, 128 + 32, 8UL);
        Put(bytes, 192 + 24, 400UL);
        Put(bytes, 256 + 4, 11U); Put(bytes, 256 + 24, 640UL); Put(bytes, 256 + 32, 48UL);
        Put(bytes, 256 + 40, 2U); Put(bytes, 256 + 56, 24UL);
        var name = Encoding.ASCII.GetBytes(FinalBridgeArtifactGuard.Getter);
        Array.Copy(name, 0, bytes, 401, name.Length);
        Put(bytes, 664, 1U); Put(bytes, 664 + 6, (ushort)1); Put(bytes, 664 + 8, 0x1000UL);
        bytes[664 + 4] = 0x12; Put(bytes, 664 + 16, 8UL);
        Put(bytes, 384, enabled ? 0x52800020U : 0x52800000U); Put(bytes, 388, 0xd65f03c0U);
        return bytes;
    }

    private static void Put(byte[] target, int offset, ushort value) => Array.Copy(BitConverter.GetBytes(value), 0, target, offset, 2);
    private static void Put(byte[] target, int offset, uint value) => Array.Copy(BitConverter.GetBytes(value), 0, target, offset, 4);
    private static void Put(byte[] target, int offset, ulong value) => Array.Copy(BitConverter.GetBytes(value), 0, target, offset, 8);
}
