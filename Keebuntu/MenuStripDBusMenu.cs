using System;
using System.Reflection;
using System.Collections.Generic;
using System.Windows.Forms;
using DBus;
using com.canonical.dbusmenu;
using System.Diagnostics;
using System.IO;
using System.Drawing.Imaging;

namespace Keebuntu
{
  public class MenuStripDBusMenu : com.canonical.dbusmenu.IDbusMenu
  {
    // TODO - replace this with a dictionary
    private List<IMenuItemProxy> mMenuItemList;
    private Form mMenuParentForm;
    private uint mRevision = 0;
    private object mLockObject = new object();

    // TODO - extract org.freedesktop.DBus.IProperties impementation to a base class
    #region org.freedesktop.DBus.IProperties

    public object Get(string @interface, string propname)
    {
      return GetPropertiesForInterface(@interface)[propname];
    }

    public IDictionary<string, object> GetAll(string @interface)
    {
      return GetPropertiesForInterface(@interface);
    }

    public void Set(string @interface, string propname, object value)
    {
      if (GetPropertiesForInterface(@interface)[propname] != value) {
        GetType().GetProperty(propname).SetValue(this, value, null);
      }
    }

    private IDictionary<string, object> GetPropertiesForInterface(string interfaceName)
    {
      var properties = new Dictionary<string, object>();
      foreach (var @interface in GetType().GetInterfaces()) {
        var interfaceAttributes =
          @interface.GetCustomAttributes(typeof(DBus.InterfaceAttribute), false);
        // TODO need to check this for DBus.InterfaceAttribute too.
        foreach (DBus.InterfaceAttribute attrib in interfaceAttributes) {
          if (string.IsNullOrWhiteSpace(interfaceName) || attrib.Name == interfaceName) {
            foreach (var property in @interface.GetProperties()) {
              // TODO - do we need to handle indexed properties?
              properties.Add(property.Name, property.GetValue(this, null));
            }
          }
        }
      }
      return properties;
    }

    #endregion

    public uint Version {
      // TODO - need to verify that this is correct
      get { return 3; }
    }

    public string TextDirection {
      get
      {
        return mMenuParentForm.RightToLeft == RightToLeft.Yes ? "rtl" : "ltr";
      }
    }

    public string Status {
      get {
        // TODO implement "notice" and property change support
        return "normal";
      }
    }

    public string[] IconThemePath {
      get { return new string[0]; }
    }

    public MenuStripDBusMenu(MenuStrip menu)
    {
      if (menu == null) {
        throw new ArgumentNullException("menu");
      }
      lock (mLockObject) {
        mMenuParentForm = menu.FindForm();

        mMenuItemList = new List<IMenuItemProxy>();

        mMenuItemList.Insert(0, new MenuStripProxy(menu));
        AddItemsToMenuItemList(menu.Items);
        if (Environment.GetEnvironmentVariable("APPMENU_DISPLAY_BOTH") != "1")
        {
          menu.Visible = false;
        }
      }
    }

    private void AddItemsToMenuItemList(ToolStripItemCollection items)
    {
      foreach (ToolStripItem item in items) {
        var itemProxy = ToolStripItemProxy.GetProxyFromCache(item);
        if (!mMenuItemList.Contains(itemProxy))
        {
          mMenuItemList.Add(itemProxy);
        }
        var dropDownItem = item as ToolStripDropDownItem;
        if (dropDownItem != null) {
          AddItemsToMenuItemList(dropDownItem.DropDownItems);
          dropDownItem.DropDown.ItemAdded += (sender, e) =>
          {
            var addedItemProxy = ToolStripItemProxy.GetProxyFromCache(e.Item);
            if (!mMenuItemList.Contains(addedItemProxy)) {
              mMenuItemList.Add(addedItemProxy);
            }
            if (dropDownItem.DropDownItems.Count == 1) {
              OnItemPropertyUpdated(addedItemProxy, "children-display");
            }
          };
          dropDownItem.DropDown.ItemRemoved += (sender, e) =>
          {
            var addedItemProxy = ToolStripItemProxy.GetProxyFromCache(e.Item);
            // set to null instead of removing because we are using the index
            // as the id and we don't want to mess it up.
            var itemIndex = mMenuItemList.IndexOf(addedItemProxy);
            mMenuItemList[itemIndex] = null;
            if (dropDownItem.DropDownItems.Count == 0) {
              OnItemPropertyUpdated(addedItemProxy, "children-display");
            }
          };
          var menuItem = dropDownItem as ToolStripMenuItem;
          var menuItemProxy = ToolStripItemProxy.GetProxyFromCache(item);
          if (menuItem != null) {
            menuItem.CheckStateChanged += (sender, e) =>
              OnItemPropertyUpdated(menuItemProxy, "toggle-state");
          }
        }
        item.TextChanged += (sender, e) =>
          OnItemPropertyUpdated(itemProxy, "label");
        item.EnabledChanged += (sender, e) =>
          OnItemPropertyUpdated(itemProxy, "enabled");
        item.AvailableChanged += (sender, e) =>
          OnItemPropertyUpdated(itemProxy, "visible");
      }
    }

    public void GetLayout(int parentId, int recursionDepth, string[] propertyNames,
                          out uint revision, out MenuItemLayout layout)
    {
#if DEBUG
      Console.WriteLine("GetLayout - parentId:{0}, " +
                        "recursionDepth:{1}, propertyNames:{2}",
                        parentId, recursionDepth,
                        string.Join(", ", propertyNames));
#endif
      if (propertyNames.Length == 0) {
        propertyNames = DefaultMenuItemProxy.GetAllDisplayNames();
      }

      revision = mRevision;

      var item = mMenuItemList[parentId];
      layout = CreateMenuItemLayout(item, 0, recursionDepth, propertyNames);
    }

    private MenuItemLayout CreateMenuItemLayout(IMenuItemProxy item,
                                                int depth, int maxDepth,
                                                string[] propertyNames)
    {
      var layout = new MenuItemLayout();
      layout.id = mMenuItemList.IndexOf(item);
      layout.properties = new Dictionary<string, object>();
      foreach (var property in propertyNames) {
        try {
          var value = item.GetValue(property);
          if (!DefaultMenuItemProxy.IsDefaultValue(property, value))
          {
            layout.properties.Add(property, value);
          }
        } catch (Exception ex) {
          Debug.Fail(ex.ToString());
        }
      }
      var childList = new List<object>();
      if (maxDepth < 0 || depth < maxDepth) {
        foreach (var childItem in item.GetChildren()) {
          childList.Add(CreateMenuItemLayout(childItem, depth + 1,
                                             maxDepth, propertyNames));
        }
      }
      layout.childeren = childList.ToArray();
      return layout;
    }

    public com.canonical.dbusmenu.MenuItem[] GetGroupProperties(int[] ids,
                                                                string[] propertyNames)
    {
#if DEBUG
      Console.WriteLine("GetGroupProperties - ids:{0}, propertyNames:{1}",
                        string.Join(", ", ids), string.Join(", ", propertyNames));
#endif
      if (propertyNames.Length == 0) {
        propertyNames = DefaultMenuItemProxy.GetAllDisplayNames();
      }
      var itemList = new List<com.canonical.dbusmenu.MenuItem>();
      foreach (var id in ids) {
        var item = mMenuItemList[id];
        var menuItem = new com.canonical.dbusmenu.MenuItem();
        menuItem.id = id;
        menuItem.properties = new Dictionary<string, object>();
        foreach (var property in propertyNames) {
          try {
            var value = item.GetValue(property);
            if (!DefaultMenuItemProxy.IsDefaultValue(property, value))
            {
              menuItem.properties.Add(property, value);
            }
          } catch (Exception ex) {
            Debug.Fail(ex.ToString());
          }
        }
        itemList.Add(menuItem);
      }

      return itemList.ToArray();
    }

    public object GetProperty(int id, string name)
    {
#if DEBUG
      Console.WriteLine("GetProperty - id:{0}, name:{1}", id, name);
#endif
      var item = mMenuItemList[id];
      return item.GetValue(name);
    }

    public void Event(int id, string eventId, object data, uint timestamp)
    {
#if DEBUG
      Console.WriteLine("Event - id:{0}, eventId:{1}, data:{2}, timestamp:{3}",
                        id, eventId, data, timestamp);
#endif
      var item = mMenuItemList[id];
      item.OnEvent(eventId, data, timestamp);
    }

    public int[] EventGroup(MenuEvent[] events)
    {
#if DEBUG
      Console.WriteLine("EventGroup - events:{0}", events.Length);
#endif
      var errorList = new List<int>();
      foreach (var @event in events) {
        try {
          Event(@event.id,  @event.eventId,  @event.data,  @event.timestamp);
        } catch (Exception) {
          errorList.Add(@event.id);
        }
      }
      return errorList.ToArray();
    }

    public bool AboutToShow(int id)
    {
#if DEBUG
      Console.WriteLine("AboutToShow - id:{0}", id);
#endif
      var item = mMenuItemList[id];
      return item.OnAboutToShow();
    }

    public void AboutToShowGroup(int[] ids, out int[] updatesNeeded, out int[] idErrors)
    {
#if DEBUG
      Console.WriteLine("AboutToShowgroup - ids:{0}", ids.Length);
#endif
      var needsUpdateList = new List<int>();
      var errorList = new List<int>();
      foreach (var id in ids) {
        try {
          if (AboutToShow(id)) {
            needsUpdateList.Add(id);
          }
        } catch (Exception) {
          errorList.Add(id);
        }
      }
      updatesNeeded = needsUpdateList.ToArray();
      idErrors = errorList.ToArray();
    }

    public event ItemsPropertiesUpdatedHandler ItemsPropertiesUpdated;

    /// <summary>
    /// Helper class for single property change.
    /// Raises the items properties updated event.
    /// </summary>
    private void OnItemPropertyUpdated(IMenuItemProxy item, string property)
    {
      // TODO - cache property values so that we don't send unnessasary events
      var properties = new com.canonical.dbusmenu.MenuItem();
      properties.id = mMenuItemList.IndexOf(item);
      properties.properties = new Dictionary<string, object>();
      properties.properties.Add(property, item.GetValue(property));
      OnItemsPropertiesUpdated(new[] { properties }, null);
      OnLayoutUpdated(mMenuItemList.IndexOf(item));
    }

    private void OnItemsPropertiesUpdated(com.canonical.dbusmenu.MenuItem[] updatedProps,
                                          MenuItemPropertyDescriptor[] removedProps)
    {
#if DEBUG
      Console.WriteLine("OnItemsPropertiesUpdated - ");
      Console.Write("  updatedProps - ");
      if (updatedProps == null) {
        Console.WriteLine("Empty");
      } else {
        foreach(var prop in updatedProps)
        {
          Console.WriteLine("id:{0}, properties:{1}",
                            prop.id, string.Join(", ", prop.properties));

        }
      }
      Console.Write("  removedProps - ");
      if (removedProps == null) {
        Console.WriteLine("Empty");
      } else {
        foreach(var prop in removedProps)
        {
          Console.WriteLine("id:{0}, properties:{1}",
                            prop.id, string.Join(", ", prop.properties));

        }
      }
#endif
      if (ItemsPropertiesUpdated != null) {
        if (updatedProps == null) {
          updatedProps = new com.canonical.dbusmenu.MenuItem[0];
        }
        if (removedProps == null) {
          removedProps = new MenuItemPropertyDescriptor[0];
        }
        foreach (var method in ItemsPropertiesUpdated.GetInvocationList()) {
          try {
            method.DynamicInvoke(updatedProps, removedProps);
          } catch (Exception ex) {
            Debug.Fail(ex.ToString());
          }
        }
      }
    }

    public event LayoutUpdatedHandler LayoutUpdated;

    private void OnLayoutUpdated(int parent)
    {
#if DEBUG
      Console.WriteLine("OnLayoutUpdated - parent:{0}", parent);
#endif
      if (LayoutUpdated != null) {
        foreach (var method in LayoutUpdated.GetInvocationList()) {
          try {
            method.DynamicInvoke(mRevision++, parent);
          } catch (Exception ex) {
            Debug.Fail(ex.ToString());
          }
        }
      }
    }

    public event ItemActivationRequestedHandler ItemActivationRequested;

    private void OnItemActivationRequested(int id)
    {
#if DEBUG
      Console.WriteLine("OnItemActivationRequested - id:{0}", id);
#endif
      if (ItemActivationRequested != null) {
        foreach (var method in ItemActivationRequested.GetInvocationList()) {
          try {
            method.DynamicInvoke(id, (uint)DateTime.Now.Ticks);
          } catch (Exception ex) {
            Debug.Fail(ex.ToString());
          }
        }
      }
    }

    private void InvokeParentForm(Action action)
    {
      if (mMenuParentForm.InvokeRequired) {
        mMenuParentForm.Invoke(action);
      } else {
        action.Invoke();
      }
    }
  }
}