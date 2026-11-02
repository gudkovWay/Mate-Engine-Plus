using System;
using System.Collections.Generic;
using UnityEngine;

namespace MateEngine.Desktop
{
    internal static class CompanionIcons
    {
        static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
        internal static Sprite Get(string name)
        {
            Sprite cached; if (icons.TryGetValue(name, out cached)) return cached;
            const int size = 64; var pixels = new Color32[size * size];
            Action<int,int,int,int> line = (x1,y1,x2,y2) =>
            {
                var a = new Vector2(x1,y1); var delta = new Vector2(x2-x1,y2-y1); float length = delta.sqrMagnitude;
                for (int y = Math.Max(0, Math.Min(y1,y2)-3); y <= Math.Min(size-1, Math.Max(y1,y2)+3); y++)
                    for (int x = Math.Max(0, Math.Min(x1,x2)-3); x <= Math.Min(size-1, Math.Max(x1,x2)+3); x++)
                    {
                        var p = new Vector2(x+.5f,y+.5f); float t = length > 0 ? Mathf.Clamp01(Vector2.Dot(p-a,delta)/length) : 0;
                        float coverage = Mathf.Clamp01(3 - Vector2.Distance(p,a+delta*t));
                        byte alpha = (byte)Mathf.RoundToInt(255*coverage); int index = y*size+x;
                        if (alpha > pixels[index].a) pixels[index] = new Color32(242,237,225,alpha);
                    }
            };
            Action<int,int,int,int> box = (x,y,w,h) => { line(x,y,x+w,y); line(x+w,y,x+w,y+h); line(x+w,y+h,x,y+h); line(x,y+h,x,y); };
            if (name == "home")
            { line(10,33,32,52); line(32,52,54,33); line(16,34,16,12); line(16,12,48,12); line(48,12,48,34); line(27,12,27,29); line(27,29,37,29); line(37,29,37,12); }
            else if (name == "actions")
            { box(11,11,16,16); box(37,11,16,16); box(11,37,16,16); box(37,37,16,16); }
            else if (name == "quiet")
            { line(17,19,20,39); line(20,39,26,46); line(26,46,38,46); line(38,46,44,39); line(44,39,47,19); line(15,18,49,18); line(28,10,36,10); line(12,53,53,10); }
            else if (name == "avatar")
            {
                for (int i = 0; i < 16; i++) { float a = i * Mathf.PI / 8, b = (i + 1) * Mathf.PI / 8; line(32 + Mathf.RoundToInt(8 * Mathf.Cos(a)), 43 + Mathf.RoundToInt(8 * Mathf.Sin(a)), 32 + Mathf.RoundToInt(8 * Mathf.Cos(b)), 43 + Mathf.RoundToInt(8 * Mathf.Sin(b))); }
                line(14,12,18,26); line(18,26,26,31); line(26,31,38,31); line(38,31,46,26); line(46,26,50,12); line(14,12,50,12);
            }
            else if (name == "back")
            { line(27,48,11,32); line(11,32,27,16); line(11,32,53,32); }
            else if (name == "forward")
            { line(37,48,53,32); line(53,32,37,16); line(11,32,53,32); }
            else if (name == "folder")
            { line(9,15,9,47); line(9,47,27,47); line(27,47,33,40); line(33,40,55,40); line(55,40,55,15); line(55,15,9,15); }
            else if (name == "new")
            { box(10,20,36,30); line(18,20,18,10); line(18,10,30,20); line(47,8,47,28); line(37,18,57,18); }
            else if (name == "factory")
            { line(9,13,9,41); line(9,41,23,32); line(23,32,23,42); line(23,42,38,32); line(38,32,38,52); line(38,52,48,52); line(48,52,48,13); line(48,13,9,13); box(16,18,7,7); box(29,18,7,7); }
            else if (name == "nodes")
            { box(8,39,14,14); box(42,25,14,14); box(8,8,14,14); line(22,46,33,46); line(33,46,33,32); line(33,32,42,32); line(22,15,33,15); line(33,15,33,32); }
            else if (name == "confirm")
            { line(12,32,26,17); line(26,17,53,48); }
            else if (name == "link")
            { line(14,16,48,50); line(29,50,48,50); line(48,50,48,31); line(14,28,14,13); line(14,13,36,13); }
            else
            { box(10,20,44,30); line(18,20,18,10); line(18,10,30,20); line(19,39,43,39); line(19,31,35,31); }
            var texture = new Texture2D(size,size,TextureFormat.RGBA32,false); texture.name = "Companion glyph " + name;
            texture.SetPixels32(pixels); texture.Apply(false,true); texture.filterMode = FilterMode.Bilinear; texture.wrapMode = TextureWrapMode.Clamp;
            cached = Sprite.Create(texture,new Rect(0,0,size,size),new Vector2(.5f,.5f),size); cached.name = name; icons.Add(name,cached); return cached;
        }
    }
}
