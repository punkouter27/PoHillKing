using UnityEngine;
using UnityEngine.UIElements;

namespace PoKingHill
{
    /// <summary>
    /// A badge's vitals strip, drawn with Painter2D: balance bar (Biometrics stability; green, amber, red), the last 3 s
    /// of joint strain as a sparkline (white, red above 60 % of the force limits). Repainted only when a value changed.
    /// </summary>
    public class VitalsBar : VisualElement
    {
        float _stab = 1f; bool _out; int _head = -1; Biometrics.Vitals _v;
        static readonly Color Good = new(0.43f, 0.84f, 0.47f), Warn = new(1f, 0.77f, 0.25f), Bad = new(0.94f, 0.38f, 0.34f), Track = new(1f, 1f, 1f, 0.14f);

        public VitalsBar() { AddToClassList("vitals"); pickingMode = PickingMode.Ignore; generateVisualContent += Draw; }

        public void Set(Biometrics.Vitals v, bool isOut)
        {
            if (v == _v && v.head == _head && Mathf.Abs(v.stability - _stab) < 0.01f && isOut == _out) return;
            _v = v; _head = v.head; _stab = v.stability; _out = isOut; MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D; var r = contentRect; if (r.width < 4 || _v == null) return;
            float w = r.width, barH = r.height * 0.32f;
            Bar(p, 0, w, barH, Track); if (!_out) Bar(p, 0, w * Mathf.Clamp01(_stab), barH, _stab > 0.6f ? Good : _stab > 0.3f ? Warn : Bad);
            // strain sparkline, oldest sample on the left
            float y0 = barH + r.height * 0.08f, sh = r.height * 0.6f;
            p.lineWidth = 2f; p.lineJoin = LineJoin.Round; p.strokeColor = _out ? Track : new Color(1f, 1f, 1f, 0.8f);
            p.BeginPath(); float peak = 0;
            for (int k = 0; k < Biometrics.History; k++)
            {
                float s = Mathf.Clamp01(_v.strainHistory[(_v.head + k) % Biometrics.History] / 0.8f); peak = Mathf.Max(peak, s);
                var pt = new Vector2(w * k / (Biometrics.History - 1), y0 + sh * (1f - s));
                if (k == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            if (peak > 0.75f && !_out) p.strokeColor = Bad;
            p.Stroke();
        }

        static void Bar(Painter2D p, float x, float w, float h, Color c)
        {
            if (w <= 0) return;
            p.fillColor = c; p.BeginPath(); p.MoveTo(new Vector2(x, 0)); p.LineTo(new Vector2(x + w, 0)); p.LineTo(new Vector2(x + w, h)); p.LineTo(new Vector2(x, h)); p.ClosePath(); p.Fill();
        }
    }

    /// <summary>Small vector icons drawn with Painter2D, so no font glyph or texture is needed.</summary>
    public class Icon : VisualElement
    {
        public enum Kind { Menu, Sliders, Play }
        readonly Kind _kind;
        public Icon(Kind kind) { _kind = kind; AddToClassList("icon"); pickingMode = PickingMode.Ignore; generateVisualContent += Draw; }

        void Draw(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D; var r = contentRect; float s = Mathf.Min(r.width, r.height); var c = r.center; float u = s * 0.18f;
            p.strokeColor = new Color(0.96f, 0.97f, 0.98f); p.lineWidth = Mathf.Max(2f, s * 0.07f); p.lineCap = LineCap.Round;
            if (_kind == Kind.Menu)
                for (int i = -1; i <= 1; i++) { p.BeginPath(); p.MoveTo(new Vector2(c.x - u * 1.4f, c.y + i * u)); p.LineTo(new Vector2(c.x + u * 1.4f, c.y + i * u)); p.Stroke(); }
            else if (_kind == Kind.Play)
            {
                p.fillColor = new Color(1f, 0.77f, 0.25f); p.BeginPath();
                p.MoveTo(new Vector2(c.x - s * 0.3f, c.y - s * 0.36f)); p.LineTo(new Vector2(c.x + s * 0.38f, c.y)); p.LineTo(new Vector2(c.x - s * 0.3f, c.y + s * 0.36f)); p.ClosePath(); p.Fill();
            }
            else
                for (int i = -1; i <= 1; i += 2)
                {
                    p.BeginPath(); p.MoveTo(new Vector2(c.x - u * 1.4f, c.y + i * u * 0.8f)); p.LineTo(new Vector2(c.x + u * 1.4f, c.y + i * u * 0.8f)); p.Stroke();
                    p.fillColor = new Color(1f, 0.77f, 0.25f); p.BeginPath(); p.Arc(new Vector2(c.x + i * u * 0.6f, c.y + i * u * 0.8f), u * 0.42f, 0, 360); p.Fill();
                }
        }
    }

    /// <summary>Procedural vector country flag badge rendered cleanly with Painter2D (#16).</summary>
    public class FlagBadge : VisualElement
    {
        string _code = "UN";

        public FlagBadge(string countryCode = "UN")
        {
            _code = string.IsNullOrEmpty(countryCode) ? "UN" : countryCode.ToUpperInvariant();
            AddToClassList("flag-badge");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        public void SetCode(string code)
        {
            string next = string.IsNullOrEmpty(code) ? "UN" : code.ToUpperInvariant();
            if (next == _code) return;
            _code = next;
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width < 4 || r.height < 4) return;
            var p = ctx.painter2D;
            float w = r.width, h = r.height;
            if (_code == "KR")
            {
                Rect(p, 0, 0, w, h, Color.white);
                p.fillColor = new Color(0.80f, 0.14f, 0.22f); p.BeginPath(); p.Arc(new Vector2(w * 0.5f, h * 0.5f), h * 0.28f, 180, 360); p.Fill();
                p.fillColor = new Color(0.08f, 0.24f, 0.58f); p.BeginPath(); p.Arc(new Vector2(w * 0.5f, h * 0.5f), h * 0.28f, 0, 180); p.Fill();
            }
            else if (_code == "US")
            {
                for (int s = 0; s < 5; s++)
                    Rect(p, 0, s * h / 5f, w, h / 5f, s % 2 == 0 ? new Color(0.76f, 0.14f, 0.20f) : Color.white);
                Rect(p, 0, 0, w * 0.46f, h * 0.56f, new Color(0.10f, 0.22f, 0.48f));
            }
            else if (_code == "GB")
            {
                Rect(p, 0, 0, w, h, new Color(0.05f, 0.18f, 0.48f));
                Rect(p, 0, h * 0.34f, w, h * 0.32f, Color.white);
                Rect(p, w * 0.38f, 0, w * 0.24f, h, Color.white);
                Rect(p, 0, h * 0.41f, w, h * 0.18f, new Color(0.80f, 0.12f, 0.18f));
                Rect(p, w * 0.43f, 0, w * 0.14f, h, new Color(0.80f, 0.12f, 0.18f));
            }
            else if (_code == "BR")
            {
                Rect(p, 0, 0, w, h, new Color(0.05f, 0.52f, 0.26f));
                p.fillColor = new Color(0.98f, 0.82f, 0.12f);
                p.BeginPath(); p.MoveTo(new Vector2(w * 0.5f, h * 0.12f)); p.LineTo(new Vector2(w * 0.88f, h * 0.5f));
                p.LineTo(new Vector2(w * 0.5f, h * 0.88f)); p.LineTo(new Vector2(w * 0.12f, h * 0.5f)); p.ClosePath(); p.Fill();
                p.fillColor = new Color(0.08f, 0.20f, 0.52f); p.BeginPath(); p.Arc(new Vector2(w * 0.5f, h * 0.5f), h * 0.18f, 0, 360); p.Fill();
            }
            else
            {
                Color a = _code == "CN" ? new Color(0.84f, 0.16f, 0.16f) : new Color(0.16f, 0.46f, 0.78f);
                Rect(p, 0, 0, w, h, a);
                p.fillColor = new Color(0.98f, 0.84f, 0.22f); p.BeginPath(); p.Arc(new Vector2(w * 0.28f, h * 0.36f), h * 0.16f, 0, 360); p.Fill();
            }
        }

        static void Rect(Painter2D p, float x, float y, float w, float h, Color c)
        {
            p.fillColor = c; p.BeginPath(); p.MoveTo(new Vector2(x, y)); p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h)); p.LineTo(new Vector2(x, y + h)); p.ClosePath(); p.Fill();
        }
    }
}
