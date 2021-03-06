using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DBus;
using KeePass.Plugins;
using KeePass.UI;
using KeePassLib;
using KeePassLib.Utility;
using Keebuntu.DBus;
using Keebuntu.Dbus;

namespace KeebuntuAppMenu
{
  public class KeebuntuAppMenuExt : Plugin
  {
    const string menuPath = "/com/canonical/menu/{0}";
    const string keebuntuAppMenuWarningSeenId = "KeebuntuAppMenu.WarningSeen";

    IPluginHost pluginHost;
    MenuStripDBusMenu dbusMenu;
    MenuStripDBusMenu emptyDBusMenu;
    com.canonical.AppMenu.Registrar.IRegistrar unityPanelServiceBus;
    IntPtr mainFormXid;
    ObjectPath mainFormObjectPath;
    bool hideMenuInApp;
    AutoResetEvent gtkInitDoneEvent;
    bool gtkInitOk = false;

    public override bool Initialize(IPluginHost host)
    {
      pluginHost = host;

      // mimmic behavior of other ubuntu apps
      hideMenuInApp =
        Environment.GetEnvironmentVariable("APPMENU_DISPLAY_BOTH") != "1";
      try {
        DBusBackgroundWorker.Start();
        gtkInitDoneEvent = new AutoResetEvent(false);
        DBusBackgroundWorker.InvokeGtkThread(() => GtkDBusInit());
        if (!gtkInitDoneEvent.WaitOne(1000))
          throw new TimeoutException("Timed out waiting for GTK thread.");
        if (!gtkInitOk)
          throw new Exception("GTK init failed.");

        if (hideMenuInApp)
        {
          pluginHost.MainWindow.MainMenu.Visible = false;
        }
        pluginHost.MainWindow.Activated += MainWindow_Activated;
        GlobalWindowManager.WindowAdded += GlobalWindowManager_WindowAdded;
        GlobalWindowManager.WindowRemoved += GlobalWindowManager_WindowRemoved;
      } catch (Exception ex) {
        Debug.Fail(ex.ToString());
        if (gtkInitOk)
          Terminate();
        return false;
      }
      return true;
    }

    public override void Terminate()
    {
      try {
        pluginHost.MainWindow.Activated -= MainWindow_Activated;
        GlobalWindowManager.WindowAdded -= GlobalWindowManager_WindowAdded;
        GlobalWindowManager.WindowRemoved -= GlobalWindowManager_WindowRemoved;
        DBusBackgroundWorker.InvokeWinformsThread(() => {
          pluginHost.MainWindow.MainMenu.Visible = true;
        });
        DBusBackgroundWorker.Stop();
      } catch (Exception ex) {
        Debug.Fail(ex.ToString());
      }
    }

    void MainWindow_Activated(object sender, EventArgs e)
    {
      if (hideMenuInApp)
      {
        pluginHost.MainWindow.MainMenu.Visible = false;
      }
      // have to re-register the window each time the main windows is shown
      // otherwise we lose the application menu
      // TODO - sometimes we invoke this unnessasarily. If there is a way to
      // test that we are still registered, that would proably be better.
      // For now, it does not seem to hurt anything.
      DBusBackgroundWorker.InvokeGtkThread(
        () => unityPanelServiceBus.RegisterWindow((uint)mainFormXid.ToInt32(),
                                                   mainFormObjectPath));
    }

    void GtkDBusInit()
    {
      /* setup ApplicationMenu */

      dbusMenu = new MenuStripDBusMenu(pluginHost.MainWindow.MainMenu);
      emptyDBusMenu = new MenuStripDBusMenu(new MenuStrip());

      var sessionBus = Bus.Session;

#if DEBUG
      const string dbusBusPath = "/org/freedesktop/DBus";
      const string dbusBusName = "org.freedesktop.DBus";
      var dbusObjectPath = new ObjectPath(dbusBusPath);
      var dbusService =
        sessionBus.GetObject<org.freedesktop.DBus.IBus>(dbusBusName, dbusObjectPath);
      dbusService.NameAcquired += (name) => Console.WriteLine ("NameAcquired: " + name);
#endif
      const string registrarBusPath = "/com/canonical/AppMenu/Registrar";
      const string registratBusName = "com.canonical.AppMenu.Registrar";
      var registrarObjectPath = new ObjectPath(registrarBusPath);
      unityPanelServiceBus =
        sessionBus.GetObject<com.canonical.AppMenu.Registrar.IRegistrar>(registratBusName,
                                                                         registrarObjectPath);
      mainFormXid = GetWindowXid(pluginHost.MainWindow);
      mainFormObjectPath = new ObjectPath(string.Format(menuPath,
                                                        mainFormXid));
      sessionBus.Register(mainFormObjectPath, dbusMenu);
      try {
        unityPanelServiceBus.RegisterWindow((uint)mainFormXid.ToInt32(),
                                            mainFormObjectPath);
        gtkInitOk = true;
        gtkInitDoneEvent.Set();
      } catch (Exception) {
        gtkInitDoneEvent.Set();
        if (!pluginHost.CustomConfig.GetBool(keebuntuAppMenuWarningSeenId, false)) {
          using (var dialog = new Gtk.Dialog()) {
            dialog.BorderWidth = 6;
            dialog.Resizable = false;
            dialog.HasSeparator = false;
            var message = "<span weight=\"bold\"size=\"larger\">"
              + "Could not register KeebuntuAppMenu with Unity panel service."
              + "</span>\n\n"
              + "This plugin only works with Ubuntu Unity desktop."
              + " If you do not use Unity, you should uninstall the KeebuntuAppMenu plugin."
              + "\n";
            var label = new Gtk.Label(message);
            label.UseMarkup = true;
            label.Wrap = true;
            label.Yalign = 0;
            var icon = new Gtk.Image(Gtk.Stock.DialogError, Gtk.IconSize.Dialog);
            icon.Yalign = 0;
            var contentBox = new Gtk.HBox();
            contentBox.Spacing = 12;
            contentBox.BorderWidth = 6;
            contentBox.PackStart(icon);
            contentBox.PackEnd(label);
            dialog.VBox.PackStart(contentBox);
            dialog.AddButton("Don't show this again", Gtk.ResponseType.Accept);
            dialog.AddButton("OK", Gtk.ResponseType.Ok);
            dialog.DefaultResponse = Gtk.ResponseType.Ok;
            dialog.Response += (o, args) => {
              dialog.Destroy();
              if (args.ResponseId == Gtk.ResponseType.Accept)
                pluginHost.CustomConfig.SetBool(keebuntuAppMenuWarningSeenId, true);
            };
            dialog.ShowAll();
            dialog.KeepAbove = true;
            dialog.Run();
          }
          DBusBackgroundWorker.Stop();
        }
      }
    }

    void GlobalWindowManager_WindowAdded(object sender, GwmWindowEventArgs e)
    {
      var xid = (uint)GetWindowXid(e.Form);
      var objectPath = new ObjectPath(string.Format(menuPath, xid));
      DBusBackgroundWorker.InvokeGtkThread(() => {
        Bus.Session.Register(objectPath, emptyDBusMenu);
        unityPanelServiceBus.RegisterWindow(xid, objectPath);
      });
    }

    void GlobalWindowManager_WindowRemoved(object sender, GwmWindowEventArgs e)
    {
      var xid = (uint)GetWindowXid(e.Form);
      var objectPath = new ObjectPath(string.Format(menuPath, xid));
      DBusBackgroundWorker.InvokeGtkThread(() => {
        unityPanelServiceBus.UnregisterWindow(xid);
        Bus.Session.Unregister(objectPath);
      });
      if (GlobalWindowManager.WindowCount <= 1)
        DBusBackgroundWorker.InvokeGtkThread(
          () => unityPanelServiceBus.RegisterWindow((uint)mainFormXid.ToInt32(),
                                                     mainFormObjectPath));
    }

    IntPtr GetWindowXid(System.Windows.Forms.Form form)
    {
      var winformsAssm = typeof(System.Windows.Forms.Control).Assembly;
      var hwndType = winformsAssm.GetType("System.Windows.Forms.Hwnd");
      var objectFromHandleMethod =
        hwndType.GetMethod("ObjectFromHandle", BindingFlags.Public | BindingFlags.Static);
      var hwnd =
        objectFromHandleMethod.Invoke(null, new object[] { form.Handle });
      var wholeWindowField = hwndType.GetField("whole_window",
                                               BindingFlags.NonPublic | BindingFlags.Instance);
      return (IntPtr)wholeWindowField.GetValue(hwnd);
    }
  }
}
