using System.Runtime.InteropServices;

namespace HPD.Sandbox.Local.Platforms.Linux.Seccomp;

/// <summary>
/// Builds BPF programs for seccomp filtering.
/// </summary>
internal static class BpfFilterBuilder
{
    /// <summary>
    /// Builds a BPF program that blocks Unix domain socket creation.
    /// </summary>
    public static BpfInstruction[] BuildUnixSocketBlockFilter()
    {
        var arch = SeccompNative.GetAuditArch();
        var socketNr = (uint)SeccompNative.GetSocketSyscallNumber();
        var socketpairNr = (uint)SeccompNative.GetSocketpairSyscallNumber();

        return
        [
            // 0: Load architecture
            BpfInstruction.LoadAbsolute(SeccompNative.SECCOMP_DATA_ARCH_OFFSET),
            
            // 1: Verify architecture (jump to KILL at instruction 10 if wrong)
            BpfInstruction.JumpIfEqual(arch, 0, 9),
            
            // 2: Load syscall number
            BpfInstruction.LoadAbsolute(SeccompNative.SECCOMP_DATA_NR_OFFSET),
            
            // 3: Check if socket() syscall
            BpfInstruction.JumpIfEqual(socketNr, 3, 0),
            
            // 4: Check if socketpair() syscall
            BpfInstruction.JumpIfEqual(socketpairNr, 2, 0),
            
            // 5: Not a socket syscall - ALLOW
            BpfInstruction.Return(SeccompNative.SECCOMP_RET_ALLOW),
            
            // 6: Load first argument (address family)
            BpfInstruction.LoadAbsolute(SeccompNative.SECCOMP_DATA_ARGS_OFFSET),
            
            // 7: Check if AF_UNIX
            BpfInstruction.JumpIfEqual(SeccompNative.AF_UNIX, 1, 0),
            
            // 8: Not AF_UNIX - ALLOW
            BpfInstruction.Return(SeccompNative.SECCOMP_RET_ALLOW),
            
            // 9: BLOCK - Return EPERM
            BpfInstruction.Return(SeccompNative.SECCOMP_RET_ERRNO | SeccompNative.EPERM),
            
            // 10: KILL - Wrong architecture
            BpfInstruction.Return(SeccompNative.SECCOMP_RET_KILL_PROCESS),
        ];
    }

    /// <summary>
    /// Validates a BPF program for basic correctness.
    /// </summary>
    public static bool ValidateProgram(BpfInstruction[] instructions)
    {
        if (instructions.Length == 0 || instructions.Length > 4096)
            return false;

        for (var i = 0; i < instructions.Length; i++)
        {
            var inst = instructions[i];
            
            if ((inst.Code & 0x07) == SeccompNative.BPF_JMP && inst.Code != SeccompNative.BPF_RET_K)
            {
                var trueTarget = i + 1 + inst.JumpTrue;
                var falseTarget = i + 1 + inst.JumpFalse;
                
                if (trueTarget >= instructions.Length || falseTarget >= instructions.Length)
                    return false;
            }
        }

        var last = instructions[^1];
        return (last.Code & 0x07) == SeccompNative.BPF_RET;
    }
}
