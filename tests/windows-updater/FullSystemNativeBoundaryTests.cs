using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Yahad.Authority;
using Yahad.Install;
namespace Yahad.Production.Tests;

// Pure crash-evidence fixtures; no SCM, profiles, IPC, ACLs or real updates.
internal static class FullSystemNativeBoundaryTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("native boundary distinguishes before RPC, admission, plan and preparation",()=>
        {
            Equal(NativeFullSystemBoundaryEvidence.Classify(false,false,false,null,false),NativeFullSystemCrashBoundary.NoRequest);
            Equal(NativeFullSystemBoundaryEvidence.Classify(true,false,false,null,false),NativeFullSystemCrashBoundary.UnadmittedRequest);
            Equal(NativeFullSystemBoundaryEvidence.Classify(true,true,false,null,false),NativeFullSystemCrashBoundary.AdmissionBeforePlan);
            Equal(NativeFullSystemBoundaryEvidence.Classify(true,true,true,null,false),NativeFullSystemCrashBoundary.PlanBeforePreparation);
            Equal(NativeFullSystemBoundaryEvidence.Classify(true,true,true,SystemBootstrapPhase.Prepared,false),NativeFullSystemCrashBoundary.Prepared);return Task.CompletedTask;
        });
        await check("native boundary never reconstructs source after bootstrap or data effects",()=>
        {
            Equal(NativeFullSystemBoundaryEvidence.Classify(true,true,false,SystemBootstrapPhase.ComponentIntent,false),NativeFullSystemCrashBoundary.EffectsPossible);
            Equal(NativeFullSystemBoundaryEvidence.Classify(true,true,false,null,true),NativeFullSystemCrashBoundary.EffectsPossible);
            Equal(NativeFullSystemBoundaryEvidence.Classify(true,false,true,null,false),NativeFullSystemCrashBoundary.EffectsPossible);return Task.CompletedTask;
        });
        await check("completion remains resumable across data terminal native done and RPC done gaps",()=>
        {
            Assert(!NativeFullSystemBoundaryEvidence.Complete(UpdateCheckpoint.Healthy,SystemBootstrapPhase.Complete,false,false,false));
            Assert(!NativeFullSystemBoundaryEvidence.Complete(UpdateCheckpoint.Healthy,SystemBootstrapPhase.Complete,true,false,false));
            Assert(NativeFullSystemBoundaryEvidence.Complete(UpdateCheckpoint.Healthy,SystemBootstrapPhase.Complete,true,true,false));
            Assert(!NativeFullSystemBoundaryEvidence.Complete(UpdateCheckpoint.EffectsReleaseIntent,SystemBootstrapPhase.Complete,true,true,false));return Task.CompletedTask;
        });
        await check("return completion requires every journal or verified no-effects evidence",()=>
        {
            foreach(var terminal in new[]{UpdateCheckpoint.FullRollbackComplete,UpdateCheckpoint.Aborted})
            {
                Assert(NativeFullSystemBoundaryEvidence.Complete(terminal,SystemBootstrapPhase.RolledBack,true,true,false));
                Assert(!NativeFullSystemBoundaryEvidence.Complete(terminal,SystemBootstrapPhase.Prepared,true,true,false));
                Assert(!NativeFullSystemBoundaryEvidence.Complete(terminal,SystemBootstrapPhase.RolledBack,false,true,false));
            }
            Assert(!NativeFullSystemBoundaryEvidence.Complete(null,null,true,true,false));
            Assert(NativeFullSystemBoundaryEvidence.Complete(null,null,true,true,true));
            Assert(!NativeFullSystemBoundaryEvidence.Complete(UpdateCheckpoint.Healthy,SystemBootstrapPhase.RolledBack,true,true,false));return Task.CompletedTask;
        });
        await check("native original plan retains exact activation bytes rather than a canonical reserialization",()=>
        {
            var raw=Encoding.UTF8.GetBytes("{\n  \"Format\" : 1, \"Phase\": \"committed\"\n}\n");
            var plan=new NativeFullSystemPlan(1,new string('a',32),new string('b',32),raw);
            var roundtrip=NativeJson.Read<NativeFullSystemPlan>(JsonSerializer.SerializeToUtf8Bytes(plan));
            Assert(roundtrip.PreviousActivation.SequenceEqual(raw));Assert(SHA256.HashData(roundtrip.PreviousActivation).SequenceEqual(SHA256.HashData(raw)));return Task.CompletedTask;
        });
    }
    private static void Assert(bool value){if(!value)throw new Exception("native boundary fixture");}
    private static void Equal<T>(T value,T expected)where T:struct,Enum=>Assert(value.Equals(expected));
}
