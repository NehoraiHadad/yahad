using System.IO;

internal static class OwnedFixtureDirectoryTests
{
    private static void Require(bool value){if(!value)throw new Exception("OWNED_FIXTURE_ASSERTION");}
    private static void Refuse(Action action)
    {
        try{action();}catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){return;}
        throw new Exception("OWNED_FIXTURE_UNEXPECTED_CLEANUP");
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("owned cleanup removes only the created root and is idempotent",()=>
        {
            var fixture=OwnedFixtureDirectory.Create(Path.GetTempPath());var sibling=OwnedFixtureDirectory.Create(Path.GetTempPath());
            try
            {
                Directory.CreateDirectory(Path.Combine(fixture.Root,"nested"));File.WriteAllText(Path.Combine(fixture.Root,"nested","payload.bin"),"synthetic");
                File.WriteAllText(Path.Combine(sibling.Root,"outside"),"keep");fixture.Cleanup(()=>true);Require(!Directory.Exists(fixture.Root));
                fixture.Cleanup(()=>throw new Exception("completed cleanup reentered"));Require(File.ReadAllText(Path.Combine(sibling.Root,"outside"))=="keep");
            }
            finally{fixture.Cleanup(()=>true);sibling.Cleanup(()=>true);}
            return Task.CompletedTask;
        });
        await check("unknown quiescence visibly retains the exact fixture",()=>
        {
            var fixture=OwnedFixtureDirectory.Create(Path.GetTempPath());
            try{File.WriteAllText(Path.Combine(fixture.Root,"payload"),"keep");Refuse(()=>fixture.Cleanup(()=>false));Require(File.ReadAllText(Path.Combine(fixture.Root,"payload"))=="keep");}
            finally{fixture.Cleanup(()=>true);}return Task.CompletedTask;
        });
        await check("a locked file fails cleanup visibly and permits an exact-root retry",()=>
        {
            var fixture=OwnedFixtureDirectory.Create(Path.GetTempPath());
            try
            {
                var path=Path.Combine(fixture.Root,"locked");File.WriteAllText(path,"keep");
                using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))Refuse(()=>fixture.Cleanup(()=>true));
                Require(Directory.Exists(fixture.Root)&&File.Exists(Path.Combine(fixture.Root,".yahad-fixture")));fixture.Cleanup(()=>true);Require(!Directory.Exists(fixture.Root));
            }
            finally{fixture.Cleanup(()=>true);}return Task.CompletedTask;
        });
        await check("replacement private root is refused despite the same marker",()=>
        {
            var fixture=OwnedFixtureDirectory.Create(Path.GetTempPath());var replacement=OwnedFixtureDirectory.Create(Path.GetTempPath());
            var saved=fixture.Root+"-saved";var originalReplacement=replacement.Root;var moved=false;var replaced=false;
            try
            {
                File.WriteAllText(Path.Combine(replacement.Root,"outside"),"keep");Directory.Move(fixture.Root,saved);moved=true;
                Directory.Move(replacement.Root,fixture.Root);replaced=true;Refuse(()=>fixture.Cleanup(()=>true));Require(File.ReadAllText(Path.Combine(fixture.Root,"outside"))=="keep");
            }
            finally
            {
                if(replaced)Directory.Move(fixture.Root,originalReplacement);if(moved)Directory.Move(saved,fixture.Root);
                fixture.Cleanup(()=>true);replacement.Cleanup(()=>true);
            }
            return Task.CompletedTask;
        });
        await check("changed ownership marker refuses before removing payload",()=>
        {
            var fixture=OwnedFixtureDirectory.Create(Path.GetTempPath());
            try
            {
                File.WriteAllText(Path.Combine(fixture.Root,"payload"),"keep");File.WriteAllText(Path.Combine(fixture.Root,".yahad-fixture"),"changed");
                Refuse(()=>fixture.Cleanup(()=>true));Require(File.ReadAllText(Path.Combine(fixture.Root,"payload"))=="keep");
            }
            finally{File.WriteAllText(Path.Combine(fixture.Root,".yahad-fixture"),"fixture-v1");fixture.Cleanup(()=>true);}return Task.CompletedTask;
        });
        await check("reparse root never traverses an outside fixture",()=>
        {
            var fixture=OwnedFixtureDirectory.Create(Path.GetTempPath());var outside=OwnedFixtureDirectory.Create(Path.GetTempPath());
            var saved=fixture.Root+"-saved";var moved=false;var linked=false;
            try
            {
                File.WriteAllText(Path.Combine(outside.Root,"sentinel"),"keep");Directory.Move(fixture.Root,saved);moved=true;
                // This synthetic case requires native symlink creation support;
                // an unavailable privilege is a fixture failure, never a pass.
                Directory.CreateSymbolicLink(fixture.Root,outside.Root);linked=true;Refuse(()=>fixture.Cleanup(()=>true));
                Require(File.ReadAllText(Path.Combine(outside.Root,"sentinel"))=="keep");
            }
            finally
            {
                if(linked)Directory.Delete(fixture.Root,false);if(moved)Directory.Move(saved,fixture.Root);
                fixture.Cleanup(()=>true);outside.Cleanup(()=>true);
            }
            return Task.CompletedTask;
        });
    }
}
