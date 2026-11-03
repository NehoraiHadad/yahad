using System.IO;
using System.Security;
using Yahad.Install;
namespace Yahad.Setup;

// Yahad\Repair: SYSTEM runs the retained Setup's --repair after each boot and once a
// day, which is what finishes an upgrade killed by a power cut with nobody at the
// machine. The task names one version's Setup; an upgrade's commit re-registers it.
internal static class RepairTask
{
    private const string Folder="Yahad",Name="Repair";
    private static dynamic Scheduler()
    {
        dynamic scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true)!)!;
        scheduler.Connect();return scheduler;
    }
    private static bool Missing(Exception error)=>error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);
    internal static string Arguments=>"--repair --result \""+Path.Combine(InstalledLayout.UpdaterRoot,"repair-result.json")+"\"";
    // The first daily run is tomorrow's: with StartWhenAvailable a boundary already
    // past would fire as soon as the task is registered, in the middle of the Setup
    // that registers it.
    internal static string Definition(string version,DateTimeOffset now)
    {
        var first=now.LocalDateTime.Date.AddDays(1).AddHours(3).AddMinutes(30).ToString("yyyy-MM-dd'T'HH:mm:ss");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>Yahad</Author><Description>Finishes or undoes an interrupted update and restores the Yahad services.</Description></RegistrationInfo>
              <Triggers>
                <BootTrigger><Delay>PT2M</Delay><Enabled>true</Enabled></BootTrigger>
                <CalendarTrigger><StartBoundary>{first}</StartBoundary><Enabled>true</Enabled><RandomDelay>PT1H</RandomDelay><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>
              </Triggers>
              <Principals><Principal id="System"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="System"><Exec><Command>{SecurityElement.Escape(InstallLocations.InstallerImage(version))}</Command><Arguments>{SecurityElement.Escape(Arguments)}</Arguments></Exec></Actions>
            </Task>
            """;
    }
    // Create-or-update as the service account named in the definition.
    internal static void Register(string version,DateTimeOffset now)
    {Scheduler().GetFolder("\\").RegisterTask("\\"+Folder+"\\"+Name,Definition(version,now),6,null,null,5,null);}
    internal static bool Matches(string version)
    {
        try
        {
            var action=Scheduler().GetFolder("\\"+Folder).GetTask(Name).Definition.Actions.Item(1);
            return (string)action.Path==InstallLocations.InstallerImage(version)&&(string)action.Arguments==Arguments;
        }
        catch(Exception error)when(Missing(error)){return false;}
    }
    // Idempotent: a retried removal finds nothing to do. A repair still running is
    // stopped first, so it cannot race the removal for the services.
    internal static void Remove()
    {
        var root=Scheduler().GetFolder("\\");dynamic folder;
        try{folder=root.GetFolder(Folder);}catch(Exception error)when(Missing(error)){return;}
        try{folder.GetTask(Name).Stop(0);folder.DeleteTask(Name,0);}catch(Exception error)when(Missing(error)){}
        root.DeleteFolder(Folder,0);
    }
}
