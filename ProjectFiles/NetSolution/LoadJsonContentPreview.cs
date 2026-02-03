#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.Retentivity;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using System.IO;
#endregion

public class LoadJsonContentPreview : BaseNetLogic
{
    public override void Start()
    {
        var jsonUri = LogicObject.GetVariable("JsonUri");
        string json = File.ReadAllText(new ResourceUri(jsonUri.Value.Value.ToString()).Uri);
        ((Label)Owner).Text = json;
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }
}
