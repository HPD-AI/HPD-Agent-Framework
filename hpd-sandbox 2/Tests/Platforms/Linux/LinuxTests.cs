using System.Runtime.InteropServices;
using HPD.Sandbox.Local.Platforms.Linux;
using HPD.Sandbox.Local.Platforms.Linux.Seccomp;
using Xunit;

namespace HPD.Sandbox.Local.Tests.Platforms.Linux;

public class BubblewrapBuilderTests
{
    [Fact]
    public void Build_IncludesNewSession()
    {
        var builder = new BubblewrapBuilder();
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--new-session", cmd);
    }

    [Fact]
    public void Build_IncludesDieWithParent()
    {
        var builder = new BubblewrapBuilder();
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--die-with-parent", cmd);
    }

    [Fact]
    public void WithReadOnlyRoot_AddsRoBind()
    {
        var builder = new BubblewrapBuilder();
        builder.WithReadOnlyRoot();
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--ro-bind", cmd);
        Assert.Contains("'/'", cmd);
    }

    [Fact]
    public void WithWritablePath_AddsBindMount()
    {
        var builder = new BubblewrapBuilder();
        builder.WithWritablePath("/tmp");
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--bind", cmd);
        Assert.Contains("'/tmp'", cmd);
    }

    [Fact]
    public void WithTmpfs_AddsTmpfsMount()
    {
        var builder = new BubblewrapBuilder();
        builder.WithTmpfs("/tmp");
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--tmpfs", cmd);
        Assert.Contains("'/tmp'", cmd);
    }

    [Fact]
    public void WithNetworkIsolation_AddsUnshareNet()
    {
        var builder = new BubblewrapBuilder();
        builder.WithNetworkIsolation();
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--unshare-net", cmd);
    }

    [Fact]
    public void WithPidIsolation_AddsUnsharePid()
    {
        var builder = new BubblewrapBuilder();
        builder.WithPidIsolation();
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--unshare-pid", cmd);
        Assert.Contains("--proc", cmd);
    }

    [Fact]
    public void WithDevices_AddsDevMount()
    {
        var builder = new BubblewrapBuilder();
        builder.WithDevices();
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--dev", cmd);
        Assert.Contains("'/dev'", cmd);
    }

    [Fact]
    public void WithEnvironmentVariable_AddsSetsEnv()
    {
        var builder = new BubblewrapBuilder();
        builder.WithEnvironmentVariable("TEST_VAR", "test_value");
        var cmd = builder.Build("echo test");
        
        Assert.Contains("--setenv", cmd);
        Assert.Contains("'TEST_VAR'", cmd);
        Assert.Contains("'test_value'", cmd);
    }

    [Fact]
    public void Build_IncludesShellAndCommand()
    {
        var builder = new BubblewrapBuilder();
        var cmd = builder.Build("echo hello", "/bin/bash");
        
        Assert.Contains("'/bin/bash'", cmd);
        Assert.Contains("-c", cmd);
        Assert.Contains("'echo hello'", cmd);
    }

    [Fact]
    public void BuildWithSetup_IncludesSetupScript()
    {
        var builder = new BubblewrapBuilder();
        var cmd = builder.BuildWithSetup("export FOO=bar", "echo $FOO");
        
        Assert.Contains("export FOO=bar", cmd);
        Assert.Contains("echo $FOO", cmd);
    }

    [Fact]
    public void BuildWithSeccomp_IncludesSeccompHelper()
    {
        var builder = new BubblewrapBuilder();
        var cmd = builder.BuildWithSeccomp("# setup", "echo test", "/path/to/apply-seccomp");
        
        Assert.Contains("/path/to/apply-seccomp", cmd);
        Assert.Contains("exec", cmd);
    }

    [Fact]
    public void FluentInterface_AllowsChaining()
    {
        var cmd = new BubblewrapBuilder()
            .WithReadOnlyRoot()
            .WithWritablePath("/tmp")
            .WithNetworkIsolation()
            .WithDevices()
            .Build("echo test");
        
        Assert.Contains("--ro-bind", cmd);
        Assert.Contains("--bind", cmd);
        Assert.Contains("--unshare-net", cmd);
        Assert.Contains("--dev", cmd);
    }

    [Fact]
    public void GetArguments_ReturnsCurrentArgs()
    {
        var builder = new BubblewrapBuilder();
        builder.WithReadOnlyRoot();
        builder.WithNetworkIsolation();
        
        var args = builder.GetArguments();
        
        Assert.Contains("--new-session", args);
        Assert.Contains("--die-with-parent", args);
        Assert.Contains("--ro-bind", args);
        Assert.Contains("--unshare-net", args);
    }
}

[Collection("Linux")] // Don't run Linux tests in parallel
public class SeccompFilterTests
{
    [Fact]
    public void IsArchitectureSupported_ReturnsTrueForX64OrArm64()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        var expected = arch is Architecture.X64 or Architecture.Arm64;
        
        Assert.Equal(expected, SeccompFilter.IsArchitectureSupported());
    }

    [SkippableFact]
    public void IsAvailable_ReturnsTrue_OnLinux()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Linux only");
        
        // On Linux with supported architecture, should be available
        if (SeccompFilter.IsArchitectureSupported())
        {
            Assert.True(SeccompFilter.IsAvailable());
        }
    }

    [SkippableFact]
    public void IsSupported_CombinesChecks()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Linux only");
        
        var expected = SeccompFilter.IsArchitectureSupported() && SeccompFilter.IsAvailable();
        Assert.Equal(expected, SeccompFilter.IsSupported);
    }

    [Fact]
    public void IsFilterApplied_ReturnsFalse_Initially()
    {
        // Note: Can't test applying filter as it's permanent for the process
        // This test just verifies the property exists and has a default value
        Assert.False(SeccompFilter.IsFilterApplied);
    }
}

public class SeccompNativeTests
{
    [Fact]
    public void Constants_HaveCorrectValues()
    {
        // Verify key constants match Linux kernel definitions
        Assert.Equal(38, SeccompNative.PR_SET_NO_NEW_PRIVS);
        Assert.Equal(22, SeccompNative.PR_SET_SECCOMP);
        Assert.Equal(2, SeccompNative.SECCOMP_MODE_FILTER);
        Assert.Equal(1, (int)SeccompNative.AF_UNIX);
    }

    [Fact]
    public void SyscallNumbers_CorrectForX64()
    {
        Assert.Equal(41, SeccompNative.SYS_socket_x64);
        Assert.Equal(53, SeccompNative.SYS_socketpair_x64);
    }

    [Fact]
    public void SyscallNumbers_CorrectForArm64()
    {
        Assert.Equal(198, SeccompNative.SYS_socket_arm64);
        Assert.Equal(199, SeccompNative.SYS_socketpair_arm64);
    }

    [Fact]
    public void AuditArch_CorrectValues()
    {
        Assert.Equal(0xc000003eu, SeccompNative.AUDIT_ARCH_X86_64);
        Assert.Equal(0xc00000b7u, SeccompNative.AUDIT_ARCH_AARCH64);
    }

    [Fact]
    public void SeccompReturnValues_HaveCorrectBits()
    {
        Assert.Equal(0x7fff0000u, SeccompNative.SECCOMP_RET_ALLOW);
        Assert.Equal(0x00050000u, SeccompNative.SECCOMP_RET_ERRNO);
        Assert.Equal(0x80000000u, SeccompNative.SECCOMP_RET_KILL_PROCESS);
    }
}

public class BpfInstructionTests
{
    [Fact]
    public void LoadAbsolute_CreatesCorrectInstruction()
    {
        var instr = BpfInstruction.LoadAbsolute(0);
        
        Assert.Equal((ushort)(SeccompNative.BPF_LD | SeccompNative.BPF_W | SeccompNative.BPF_ABS), instr.Code);
        Assert.Equal((byte)0, instr.Jt);
        Assert.Equal((byte)0, instr.Jf);
        Assert.Equal(0u, instr.K);
    }

    [Fact]
    public void JumpIfEqual_CreatesCorrectInstruction()
    {
        var instr = BpfInstruction.JumpIfEqual(42, 2, 1);
        
        Assert.Equal((ushort)(SeccompNative.BPF_JMP | SeccompNative.BPF_JEQ | SeccompNative.BPF_K), instr.Code);
        Assert.Equal((byte)2, instr.Jt);
        Assert.Equal((byte)1, instr.Jf);
        Assert.Equal(42u, instr.K);
    }

    [Fact]
    public void Return_CreatesCorrectInstruction()
    {
        var instr = BpfInstruction.Return(SeccompNative.SECCOMP_RET_ALLOW);
        
        Assert.Equal((ushort)(SeccompNative.BPF_RET | SeccompNative.BPF_K), instr.Code);
        Assert.Equal(SeccompNative.SECCOMP_RET_ALLOW, instr.K);
    }

    [Fact]
    public void BpfInstruction_HasCorrectSize()
    {
        // BPF instruction should be 8 bytes
        Assert.Equal(8, Marshal.SizeOf<BpfInstruction>());
    }
}

public class SeccompChildProcessTests
{
    [SkippableFact]
    public async Task EnsureHelperAsync_ReturnsPath()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Linux only");
        
        var helper = new SeccompChildProcess();
        
        try
        {
            var path = await helper.EnsureHelperAsync();
            
            Assert.NotNull(path);
            Assert.True(File.Exists(path) || path.Contains("apply-seccomp"));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("gcc"))
        {
            // gcc not installed - skip
            Skip.If(true, "gcc not installed");
        }
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var helper = new SeccompChildProcess();
        Assert.NotNull(helper);
        helper.Dispose();
    }
}

// Helper attribute for skippable tests
public class SkippableFactAttribute : FactAttribute { }

public static class Skip
{
    public static void If(bool condition, string reason = "")
    {
        if (condition)
            throw new Xunit.SkipException(reason);
    }

    public static void IfNot(bool condition, string reason = "")
    {
        if (!condition)
            throw new Xunit.SkipException(reason);
    }
}
