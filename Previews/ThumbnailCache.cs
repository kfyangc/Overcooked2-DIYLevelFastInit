using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit
{
    internal static class ThumbnailCache
    {
        internal sealed class Entry
        {
            internal string Key;
            internal Texture2D Texture;
            internal Sprite Sprite;
            internal long SourceLength;
            internal long SourceModified;
            internal int Generation;
            internal long EstimatedBytes;
            internal long Used;
        }

        private const int MaxWidth = DirectCoverReader.MaxWidth;
        private const int MaxHeight = DirectCoverReader.MaxHeight;
        private const long MaxMemoryBytes = 24L * 1024 * 1024;
        private const int MaxMemoryEntries = 64;

        private static readonly Dictionary<string, Entry> Memory =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static int _generation;
        private static long _use;
        private static long _memoryBytes;
        private static Image _image;
        private static string _boundKey;

        internal static void Invalidate()
        {
            _generation++;
            DirectCoverReader.ClearCache();
        }

        internal static bool HasWriteCapacity { get { return true; } }
        internal static bool WritesBusy { get { return false; } }

        private static string MemoryKey(string path, string scene)
        {
            return Path.GetFullPath(path).ToLowerInvariant() + "\n" + (scene ?? string.Empty).ToLowerInvariant();
        }

        private static bool TryGetSnapshotStamp(string path, out long length, out long modified)
        {
            SetSnapshot snap;
            if (FastInitPlugin.Snapshots.TryGetValue(path, out snap) && snap != null && snap.InfoFileName != null)
            {
                length = snap.InfoLength;
                modified = snap.InfoMtimeUtc.Ticks;
                return true;
            }
            length = 0;
            modified = 0;
            return false;
        }

        internal static Entry TryGetMemory(string path, string scene)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(scene)) return null;
            try
            {
                string key = MemoryKey(path, scene);
                Entry entry;
                if (!Memory.TryGetValue(key, out entry)) return null;
                long snapLength, snapModified;
                if (TryGetSnapshotStamp(path, out snapLength, out snapModified))
                {
                    if (entry.SourceLength != snapLength || entry.SourceModified != snapModified)
                    {
                        RemoveEntry(entry);
                        return null;
                    }
                }
                else if (entry.Generation != _generation)
                {
                    RemoveEntry(entry);
                    return null;
                }
                entry.Used = ++_use;
                return entry;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static Entry TryGet(string path, string scene, out bool memoryHit)
        {
            Entry entry = TryGetMemory(path, scene);
            memoryHit = entry != null;
            return entry;
        }

        internal static bool IsReady(string path, string scene)
        {
            return TryGetMemory(path, scene) != null;
        }

        internal static Entry StoreDirect(string path, string scene, DirectCoverData data)
        {
            if (data == null) return null;
            string key = MemoryKey(path, scene);
            if (data.Status == DirectCoverStatus.NoScreenshot)
            {
                return Store(key, null, default(Rect), data.SourceLength, data.SourceModified, 0);
            }
            if (data.Status != DirectCoverStatus.Ok || data.RawData == null) return null;

            TextureFormat format = (TextureFormat)data.TextureFormat;
            if (!SystemInfo.SupportsTextureFormat(format)) return null;

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(data.Width, data.Height, format, false);
                texture.LoadRawTextureData(data.RawData);
                texture.Apply(false, true);
                texture.wrapMode = TextureWrapMode.Clamp;
                Rect spriteRect = new Rect(data.RectX, data.RectY, data.RectWidth, data.RectHeight);
                Entry entry = Store(key, texture, spriteRect, data.SourceLength, data.SourceModified, data.RawData.Length);
                texture = null;
                return entry;
            }
            finally
            {
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        internal static Entry Capture(string path, string scene, Sprite source, bool retain = true)
        {
            long sourceLength, sourceModified;
            TryGetSnapshotStamp(path, out sourceLength, out sourceModified);
            string key = MemoryKey(path, scene);
            if (source == null)
                return retain ? Store(key, null, default(Rect), sourceLength, sourceModified, 0) : null;

            if (source.packed && source.packingRotation != SpritePackingRotation.None)
                throw new IOException("Rotated packed screenshot cannot be cached");
            Rect rect = source.textureRect;
            if (rect.width <= 0 || rect.height <= 0) throw new IOException("Empty screenshot rectangle");

            float scale = Mathf.Min(1f, Mathf.Min(MaxWidth / rect.width, MaxHeight / rect.height));
            int width = Math.Max(1, Mathf.RoundToInt(rect.width * scale));
            int height = Math.Max(1, Mathf.RoundToInt(rect.height * scale));
            Texture2D texture = null;
            try
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                try
                {
                    Texture2D original = source.texture;
                    Graphics.Blit(original, target, new Vector2(rect.width / original.width, rect.height / original.height),
                        new Vector2(rect.x / original.width, rect.y / original.height));
                    RenderTexture.active = target;
                    texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    texture.Apply(false, true);
                    texture.wrapMode = TextureWrapMode.Clamp;
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(target);
                }
                if (!retain) return null;
                Entry entry = Store(key, texture, new Rect(0, 0, width, height), sourceLength, sourceModified, (long)width * height * 4L);
                texture = null;
                return entry;
            }
            finally
            {
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static Entry Store(string key, Texture2D texture, Rect spriteRect,
            long sourceLength, long sourceModified, long estimatedBytes)
        {
            Sprite sprite = null;
            if (texture != null)
            {
                texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                sprite = Sprite.Create(texture, spriteRect, new Vector2(0.5f, 0.5f));
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            Entry entry = new Entry
            {
                Key = key,
                Texture = texture,
                Sprite = sprite,
                SourceLength = sourceLength,
                SourceModified = sourceModified,
                Generation = _generation,
                EstimatedBytes = estimatedBytes,
                Used = ++_use
            };
            Entry replaced;
            if (Memory.TryGetValue(key, out replaced))
            {
                _memoryBytes -= replaced.EstimatedBytes;
                Destroy(replaced);
            }
            Memory[key] = entry;
            _memoryBytes += estimatedBytes;
            TrimMemory();
            return entry;
        }

        internal static void Show(Entry entry, Image image)
        {
            if (_image != null && _image != image) Hide(_image);
            _image = image;
            _boundKey = entry.Key;
            image.sprite = entry.Sprite;
            image.gameObject.SetActive(entry.Sprite != null);
            entry.Used = ++_use;
            TrimMemory();
        }

        internal static void Hide(Image image)
        {
            if (image == null) image = _image;
            if (image != null)
            {
                image.sprite = null;
                image.gameObject.SetActive(false);
            }
            if (image == _image)
            {
                _image = null;
                _boundKey = null;
            }
        }

        private static void RemoveEntry(Entry entry)
        {
            Memory.Remove(entry.Key);
            _memoryBytes -= entry.EstimatedBytes;
            if (_memoryBytes < 0) _memoryBytes = 0;
            if (string.Equals(_boundKey, entry.Key, StringComparison.OrdinalIgnoreCase)) Hide(null);
            Destroy(entry);
        }

        private static void TrimMemory()
        {
            while (Memory.Count > MaxMemoryEntries || _memoryBytes > MaxMemoryBytes)
            {
                Entry oldest = null;
                foreach (Entry entry in Memory.Values)
                    if (entry.Key != _boundKey && (oldest == null || entry.Used < oldest.Used)) oldest = entry;
                if (oldest == null) break;
                Memory.Remove(oldest.Key);
                _memoryBytes -= oldest.EstimatedBytes;
                if (_memoryBytes < 0) _memoryBytes = 0;
                Destroy(oldest);
            }
        }

        internal static void ClearMemory()
        {
            Hide(null);
            foreach (Entry entry in Memory.Values) Destroy(entry);
            Memory.Clear();
            _memoryBytes = 0;
        }

        private static void Destroy(Entry entry)
        {
            if (entry.Sprite != null) UnityEngine.Object.DestroyImmediate(entry.Sprite);
            if (entry.Texture != null) UnityEngine.Object.DestroyImmediate(entry.Texture);
        }
    }
}
