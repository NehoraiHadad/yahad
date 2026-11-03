using System.IO;
namespace Yahad.Setup;

// The administrator's update choice, captured once by the window or the quiet
// command line and kept in the installer record for the life of the installation.
internal sealed record UpdateSetupChoice(int ConsentVersion,bool Accepted,bool Automatic,long ApprovedUnix)
{
    internal void Require(DateTimeOffset now)
    {RequirePersisted(now);if(ApprovedUnix<now.ToUnixTimeSeconds()-1800)throw new IOException("UPDATE_CONSENT_REQUIRED");}
    internal void RequirePersisted(DateTimeOffset now)
    {if(ConsentVersion!=2||!Accepted||ApprovedUnix<=0||ApprovedUnix>now.ToUnixTimeSeconds()+300)throw new IOException("UPDATE_CONSENT_REQUIRED");}
    internal void RequireCompatibleRetry(UpdateSetupChoice? requested)
    {if(requested is not null&&(requested.ConsentVersion!=ConsentVersion||requested.Accepted!=Accepted||requested.Automatic!=Automatic))throw new IOException("UPDATE_CONSENT_ALREADY_CAPTURED");}
}
// The first family owner the setup capability binds to this installation and
// to the administrator who ran Setup. The application reads it as firstOwner.
internal sealed record FirstOwnerBinding(int format,string installationId,string ownerSid,string familyId,string parentUserId)
{
    internal static FirstOwnerBinding Create(string installation,string owner)
        =>new(1,installation,owner,Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"));
    internal void Require(string installation,string owner)
    {
        if(format!=1||installationId!=installation||ownerSid!=owner||!Guid.TryParseExact(familyId,"D",out var f)||f.ToString("D")!=familyId||!Guid.TryParseExact(parentUserId,"D",out var p)||p.ToString("D")!=parentUserId||f==p)throw new IOException("UPDATE_OWNER_BINDING");
    }
}
