using System.Xml.Linq;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class StartupTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TaskXml_IsWellFormedTaskSchedulerXml()
    {
        var xml = StartupTask.TaskXml(@"PC\shamash", @"C:\Users\shamash\AppData\Local\Shamash.Pc\Shamash.Pc.exe", @"C:\Users\shamash\AppData\Local\Shamash.Pc");
        var doc = XDocument.Parse(xml);
        Assert.Equal(Ns + "Task", doc.Root!.Name);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
    }

    [Fact]
    public void TaskXml_LogonTriggerForTheUser_NoTimeLimit_LeastPrivilege()
    {
        var doc = XDocument.Parse(StartupTask.TaskXml(@"PC\shamash", @"C:\x\Shamash.Pc.exe", @"C:\x"));
        var trigger = doc.Root!.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;
        Assert.Equal(@"PC\shamash", trigger.Element(Ns + "UserId")!.Value);
        var settings = doc.Root.Element(Ns + "Settings")!;
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        var principal = doc.Root.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
    }

    [Fact]
    public void TaskXml_RunsTheStubFromItsDirectory()
    {
        var doc = XDocument.Parse(StartupTask.TaskXml("u", @"C:\x\Shamash.Pc.exe", @"C:\x"));
        var exec = doc.Root!.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal(@"C:\x\Shamash.Pc.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal(@"C:\x", exec.Element(Ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void TaskXml_EscapesSpecialCharacters()
    {
        var doc = XDocument.Parse(StartupTask.TaskXml("DOM\\a&b", @"C:\Program Files <x>\Shamash.Pc.exe", @"C:\Program Files <x>"));
        Assert.Equal("DOM\\a&b", doc.Root!.Element(Ns + "Principals")!.Element(Ns + "Principal")!.Element(Ns + "UserId")!.Value);
    }
}
