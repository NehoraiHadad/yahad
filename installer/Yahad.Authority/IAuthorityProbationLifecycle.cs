using Yahad.Service;
namespace Yahad.Authority;

// This separate trusted purpose is mandatory. An ordinary runtime lifecycle
// cannot be substituted for a provider/HTTP-fenced candidate start.
internal interface IAuthorityProbationLifecycle:IAuthorityRuntimeLifecycle,IProbationEffectsRuntime
{Task StartProbationAsync(ProbationRuntimeBinding binding,CancellationToken cancellation);}
