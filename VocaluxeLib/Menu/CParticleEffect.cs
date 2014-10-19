﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml.Serialization;
using VocaluxeLib.Draw;
using VocaluxeLib.Xml;

namespace VocaluxeLib.Menu
{
    [XmlType("ParticleEffect")]
    public struct SThemeParticleEffect
    {
        [XmlAttribute(AttributeName = "Name")] public string Name;

        public string Skin;
        public SRectF Rect;
        public SThemeColor Color;
        public EParticleType Type;
        public float Size;
        public int MaxNumber;
    }

    public enum EParticleType
    {
        Twinkle,
        Star,
        Snow,
        Flare,
        PerfNoteStar
    }

    public sealed class CParticleEffect : CMenuElementBase, IMenuElement, IThemeable
    {
        private readonly int _PartyModeID;
        private SThemeParticleEffect _Theme;
        private bool _ThemeLoaded;

        public CTextureRef Texture;
        public SColorF Color;

        private readonly List<CParticle> _Stars;
        private readonly Stopwatch _SpawnTimer;
        private float _NextSpawnTime;

        public float Alpha = 1f;

        public string GetThemeName()
        {
            return _Theme.Name;
        }

        public bool ThemeLoaded
        {
            get { return _ThemeLoaded; }
        }

        public bool IsAlive
        {
            get { return _Stars.Count > 0 || !_SpawnTimer.IsRunning; }
        }

        public CParticleEffect(int partyModeID)
        {
            _PartyModeID = partyModeID;
            _Theme = new SThemeParticleEffect();
            _Stars = new List<CParticle>();
            _SpawnTimer = new Stopwatch();
            _NextSpawnTime = 0f;
            Visible = true;
        }

        public CParticleEffect(int partyModeID, int maxNumber, SColorF color, SRectF rect, string skin, float size, EParticleType type)
        {
            _PartyModeID = partyModeID;
            _Theme = new SThemeParticleEffect();
            _Stars = new List<CParticle>();
            MaxRect = rect;
            Color = color;
            _Theme.Skin = skin;
            _Theme.MaxNumber = maxNumber;
            _Theme.Size = size;
            _Theme.Type = type;
            _SpawnTimer = new Stopwatch();
            _NextSpawnTime = 0f;
            Visible = true;
        }

        public CParticleEffect(int partyModeID, int maxNumber, SColorF color, SRectF rect, CTextureRef texture, float size, EParticleType type)
        {
            _PartyModeID = partyModeID;
            _Theme = new SThemeParticleEffect();
            _Stars = new List<CParticle>();
            MaxRect = rect;
            Color = color;
            _Theme.Skin = String.Empty;
            Texture = texture;
            _Theme.MaxNumber = maxNumber;
            _Theme.Size = size;
            _Theme.Type = type;
            _SpawnTimer = new Stopwatch();
            _NextSpawnTime = 0f;
            Visible = true;
        }

        public CParticleEffect(SThemeParticleEffect theme, int partyModeID)
        {
            _PartyModeID = partyModeID;
            _Theme = theme;
            _Stars = new List<CParticle>();
            _SpawnTimer = new Stopwatch();
            _NextSpawnTime = 0f;
            Visible = true;

            LoadSkin();
        }

        public bool LoadTheme(string xmlPath, string elementName, CXMLReader xmlReader)
        {
            string item = xmlPath + "/" + elementName;
            _ThemeLoaded = true;

            _ThemeLoaded &= xmlReader.GetValue(item + "/Skin", out _Theme.Skin, String.Empty);

            _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/X", ref _Theme.Rect.X);
            _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/Y", ref _Theme.Rect.Y);
            _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/Z", ref _Theme.Rect.Z);
            _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/W", ref _Theme.Rect.W);
            _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/H", ref _Theme.Rect.H);

            if (xmlReader.GetValue(item + "/Color", out _Theme.Color.Name, String.Empty))
                _ThemeLoaded &= _Theme.Color.Get(_PartyModeID, out Color);
            else
            {
                _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/R", ref Color.R);
                _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/G", ref Color.G);
                _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/B", ref Color.B);
                _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/A", ref Color.A);
            }

            _ThemeLoaded &= xmlReader.TryGetEnumValue(item + "/Type", ref _Theme.Type);
            _ThemeLoaded &= xmlReader.TryGetFloatValue(item + "/Size", ref _Theme.Size);
            _ThemeLoaded &= xmlReader.TryGetIntValue(item + "/MaxNumber", ref _Theme.MaxNumber);

            if (_ThemeLoaded)
            {
                _Theme.Name = elementName;
                _Theme.Color.Color = Color;
                LoadSkin();
            }
            return _ThemeLoaded;
        }

        public void Update()
        {
            bool doSpawn = false;
            if (!_SpawnTimer.IsRunning)
            {
                _SpawnTimer.Start();
                _NextSpawnTime = 0f;
                doSpawn = true;
            }

            if (_SpawnTimer.ElapsedMilliseconds / 1000f > _NextSpawnTime && _NextSpawnTime >= 0f)
            {
                doSpawn = true;
                _SpawnTimer.Reset();
                _SpawnTimer.Start();
            }

            while (_Stars.Count < _Theme.MaxNumber && doSpawn)
            {
                float size = CBase.Game.GetRandom((int)_Theme.Size / 2) + _Theme.Size / 2;
                float lifetime = 0f;
                float vx = 0f;
                float vy = 0f;
                float vr = 0f;
                float vsize = 0f;
                _NextSpawnTime = 0f;

                switch (_Theme.Type)
                {
                    case EParticleType.Twinkle:
                        size = CBase.Game.GetRandom((int)_Theme.Size / 2) + _Theme.Size / 2;
                        lifetime = CBase.Game.GetRandom(500) / 1000f + 0.5f;
                        vx = -CBase.Game.GetRandom(10000) / 50f + 100f;
                        vy = -CBase.Game.GetRandom(10000) / 50f + 100f;
                        vr = -CBase.Game.GetRandom(500) / 100f + 2.5f;
                        vsize = lifetime * 2f;
                        break;

                    case EParticleType.Star:
                        size = CBase.Game.GetRandom((int)_Theme.Size / 2) + _Theme.Size / 2;
                        lifetime = CBase.Game.GetRandom(1000) / 500f + 0.2f;
                        vx = -CBase.Game.GetRandom(1000) / 50f + 10f;
                        vy = -CBase.Game.GetRandom(1000) / 50f + 10f;
                        vr = -CBase.Game.GetRandom(500) / 100f + 2.5f;
                        vsize = lifetime * 2f;
                        break;

                    case EParticleType.Snow:
                        size = CBase.Game.GetRandom((int)_Theme.Size / 2) + _Theme.Size / 2;
                        lifetime = CBase.Game.GetRandom(5000) / 50f + 10f;
                        vx = -CBase.Game.GetRandom(1000) / 50f + 10f;
                        vy = CBase.Game.GetRandom(1000) / 50f + Math.Abs(vx) + 10f;
                        vr = -CBase.Game.GetRandom(200) / 50f + 2f;
                        vsize = lifetime * 2f;

                        _NextSpawnTime = lifetime / _Theme.MaxNumber;
                        doSpawn = false;
                        break;

                    case EParticleType.Flare:
                        size = CBase.Game.GetRandom((int)_Theme.Size / 2) + _Theme.Size / 2;
                        lifetime = CBase.Game.GetRandom(500) / 1000f + 0.1f;
                        vx = -CBase.Game.GetRandom(2000) / 50f;
                        vy = -CBase.Game.GetRandom(2000) / 50f + 20f;
                        vr = -CBase.Game.GetRandom(2000) / 50f + 20f;
                        vsize = lifetime * 2f;
                        break;

                    case EParticleType.PerfNoteStar:
                        size = CBase.Game.GetRandom((int)_Theme.Size / 2) + _Theme.Size / 2;
                        lifetime = CBase.Game.GetRandom(1000) / 500f + 1.2f;
                        vx = 0f;
                        vy = 0f;
                        vr = CBase.Game.GetRandom(500) / 50f + 10f;
                        vsize = lifetime * 2f;
                        break;
                }

                var w = (int)(Rect.W - size / 4f);
                var h = (int)(Rect.H - size / 4f);

                if (w < 0)
                    w = 0;

                if (h < 0)
                    h = 0;

                CParticle star;
                if (!String.IsNullOrEmpty(_Theme.Skin))
                {
                    star = new CParticle(_PartyModeID, _Theme.Skin, Color,
                                         CBase.Game.GetRandom(w) + Rect.X - size / 4f,
                                         CBase.Game.GetRandom(h) + Rect.Y - size / 4f,
                                         size, lifetime, Rect.Z, vx, vy, vr, vsize, _Theme.Type);
                }
                else
                {
                    star = new CParticle(_PartyModeID, Texture, Color,
                                         CBase.Game.GetRandom(w) + Rect.X - size / 4f,
                                         CBase.Game.GetRandom(h) + Rect.Y - size / 4f,
                                         size, lifetime, Rect.Z, vx, vy, vr, vsize, _Theme.Type);
                }

                _Stars.Add(star);
            }

            if (_Theme.Type == EParticleType.Flare || _Theme.Type == EParticleType.PerfNoteStar || _Theme.Type == EParticleType.Twinkle)
                _NextSpawnTime = -1f;

            int i = 0;
            while (i < _Stars.Count)
            {
                _Stars[i].Update();
                if (!_Stars[i].IsAlive)
                    _Stars.RemoveAt(i);
                else
                    i++;
            }
        }

        public void Pause()
        {
            foreach (CParticle star in _Stars)
                star.Pause();
        }

        public void Resume()
        {
            foreach (CParticle star in _Stars)
                star.Resume();
        }

        public void Draw()
        {
            Update();
            foreach (CParticle star in _Stars)
            {
                star.Alpha2 = Alpha;
                star.Draw();
            }
        }

        public void UnloadSkin()
        {
            Texture = null;
        }

        public void LoadSkin()
        {
            _Theme.Color.Get(_PartyModeID, out Color);

            if (!String.IsNullOrEmpty(_Theme.Skin))
                Texture = CBase.Themes.GetSkinTexture(_Theme.Skin, _PartyModeID);

            MaxRect = _Theme.Rect;
        }

        public void ReloadSkin()
        {
            UnloadSkin();
            LoadSkin();
        }

        public SThemeParticleEffect GetTheme()
        {
            return _Theme;
        }

        #region ThemeEdit
        public bool Selectable
        {
            get { return false; }
        }

        public void MoveElement(int stepX, int stepY)
        {
            X += stepX;
            Y += stepY;

            _Theme.Rect.X += stepX;
            _Theme.Rect.Y += stepY;
        }

        public void ResizeElement(int stepW, int stepH)
        {
            W += stepW;
            if (W <= 0)
                W = 1;

            H += stepH;
            if (H <= 0)
                H = 1;

            _Theme.Rect.W = Rect.W;
            _Theme.Rect.H = Rect.H;
        }
        #endregion ThemeEdit
    }
}