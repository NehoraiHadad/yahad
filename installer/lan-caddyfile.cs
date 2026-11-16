#:project Yahad.Install.Core/Yahad.Install.Core.csproj
// Writes the Caddyfile a home-network enable writes, from LanDeployment itself, for
// machine acceptance's PlaceLanProxy: the fixture places the product's configuration,
// never a copy of it.
// lan-caddyfile <output> <installationId> <adapterId> <address> <appPort> <caddy> <data> <joinPublic> <httpHelp>
using System.Text;
using Yahad.Install;

if (args.Length != 9) { Console.Error.WriteLine("usage: lan-caddyfile <output> <installationId> <adapterId> <address> <appPort> <caddy> <data> <joinPublic> <httpHelp>"); return 2; }
var plan = LanDeployment.Create(args[1], [new LanChoice(args[2], "selected", args[3])], args[2], args[3],
    int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), args[5], args[6], args[7], bool.Parse(args[8]));
File.WriteAllBytes(args[0], Encoding.UTF8.GetBytes(plan.Caddyfile));
return 0;
