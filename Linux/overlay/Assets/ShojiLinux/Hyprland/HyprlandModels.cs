using System;
using UnityEngine;
using Newtonsoft.Json;
using System.Collections.Generic;
using static APIs.Hyprland.HyprlandUtils;

namespace APIs.Hyprland
{
    [Serializable]
    public class HyprlandClients
    {
        public HyprlandClient[] clients;
        public int hashCode;
    }

    [Serializable]
    public class HyprlandMonitors
    {
        public HyprlandMonitor[] monitors;
    }

    [Serializable]
    public class HyprlandMonitor
    {
        public int id;
        public string name;
        public string description;
        public string make;
        public int width;
        public int height;
        public float scale = 1f;
        public int transform;
        public bool focused;
        public bool dpmsStatus;
        public int[] reserved;
        public Vector2Int size => new Vector2Int(
            Mathf.RoundToInt((transform % 2 == 0 ? width : height) / Math.Max(scale, 0.1f)),
            Mathf.RoundToInt((transform % 2 == 0 ? height : width) / Math.Max(scale, 0.1f)));
        public RectInt workArea
        {
            get
            {
                var logical = size;
                var left = reserved != null && reserved.Length == 4 ? reserved[0] : 0;
                var top = reserved != null && reserved.Length == 4 ? reserved[1] : 0;
                var right = reserved != null && reserved.Length == 4 ? reserved[2] : 0;
                var bottom = reserved != null && reserved.Length == 4 ? reserved[3] : 0;
                return new RectInt(x + left, y + top, Math.Max(1, logical.x - left - right),
                    Math.Max(1, logical.y - top - bottom));
            }
        }
        public int x;
        public int y;
        public Vector2Int position => new Vector2Int(x, y);
        public HyprlandWorkspace activeWorkspace;
        public bool disabled;
    }

    [Serializable]
    public class HyprlandWorkspace
    {
        public int id;
        public string name;
        public string type;
        public bool IsSpecial => id < 0 || type == "special" ||
            name != null && name.StartsWith("special:", StringComparison.Ordinal);
        public bool Matches(HyprlandWorkspace other) => other != null && IsSpecial == other.IsSpecial &&
            (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(other.name) ? name == other.name : id == other.id);
    }

    [Serializable]
    public class HyprlandClient
    {
        public int[] at;
        public Vector2Int atVector => new Vector2Int(at[0], at[1]);
        public int[] size;
        public Vector2Int sizeVector => new Vector2Int(size[0], size[1]);
        public string address;
        public IntPtr addressIntPtr => AddressToIntPtr(address);
        public string initialClass;
        public int pid;
        public bool floating;
        public int monitor;
        public int fullscreen;
        public string title;
        public bool hidden;
        public bool mapped;
        public bool pinned;
        public bool xwayland;
        public HyprlandWorkspace workspace;
        public int focusHistoryID;
    }

    [JsonObject]
    public class HyprlandLayerLevels
    {
        [JsonProperty("levels")]
        public Dictionary<string, List<HyprlandLayer>> levels { get; set; }
    }

    [JsonObject]
    public class HyprlandLayer
    {
        [JsonProperty("address")]
        public string address;
        public IntPtr addressIntPtr =>  AddressToIntPtr(address);

        [JsonProperty("x")]
        public int x;
        [JsonProperty("y")]
        public int y;
        public Vector2Int posVector => new Vector2Int(x, y);

        [JsonProperty("w")]
        public int w;
        [JsonProperty("h")]
        public int h;
        public Vector2Int sizeVector => new Vector2Int(w, h);

        [JsonProperty("pid")]
        public int pid;

        [JsonProperty("namespace")]
        public string name;
    }
}