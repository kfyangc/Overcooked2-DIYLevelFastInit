using System;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit.ResourceFailures
{
    // Ported from HostUtilities' HostDialogLayout: the game pools T17DialogBox instances, so this adjusts only the
    // instance used for our failure popup, grows the visible panel by the height the message actually needs, and
    // restores every captured value when the object is hidden. Other dialogs and the shared prefab stay untouched.
    internal sealed class FailureDialogLayout : MonoBehaviour
    {
        private sealed class RectState
        {
            internal readonly RectTransform Rect;
            private readonly Vector2 size, position, min, max, pivot;
            private readonly Transform parent;
            private readonly int sibling;

            internal RectState(RectTransform rect)
            {
                Rect = rect; size = rect.sizeDelta; position = rect.anchoredPosition;
                min = rect.anchorMin; max = rect.anchorMax; pivot = rect.pivot;
                parent = rect.parent; sibling = rect.GetSiblingIndex();
            }

            internal void Restore()
            {
                if (Rect.parent != parent) Rect.SetParent(parent, false);
                Rect.SetSiblingIndex(sibling);
                Rect.anchorMin = min; Rect.anchorMax = max; Rect.pivot = pivot;
                Rect.sizeDelta = size; Rect.anchoredPosition = position;
            }
        }

        private T17DialogBox dialog;
        private T17Text message;
        private T17Text title;
        private RectTransform root, holder;
        private RectState messageState, backingState, headerState, buttonsState;
        private FailureDialogBackground background;
        private bool requested, originalBestFit;
        private VerticalWrapMode originalOverflow;
        private TextAnchor originalAlignment;
        private float messageHeight, backingHeight;
        private float panelFill = 1f, panelBottomInset, panelTopInset;
        private string lastText;
        private Vector2 lastRootSize;
        private Vector3 lastPosition, lastScale;

        private float nextCheck;

        internal static bool IsApplied(T17DialogBox target)
        {
            FailureDialogLayout layout = target == null ? null : target.GetComponent<FailureDialogLayout>();
            return layout != null && layout.requested;
        }

        internal static void Apply(T17DialogBox target)
        {
            if (target == null || target.m_Message == null) return;
            try
            {
                FailureDialogLayout layout = target.GetComponent<FailureDialogLayout>();
                if (layout == null) layout = target.gameObject.AddComponent<FailureDialogLayout>();
                if (layout.message == null && !layout.Capture(target)) return;
                layout.requested = true;
                layout.lastText = null;
            }
            catch (Exception ex) { BlockerLog.Warning("Popup", "DIALOG_LAYOUT_APPLY_FAILED", ex.ToString()); }
        }

        private bool Capture(T17DialogBox target)
        {
            RectTransform holderRect = target.m_Message.rectTransform.parent as RectTransform;
            if (holderRect == null) return false;
            RectTransform backing = holderRect.Find("BackingPanel") as RectTransform;
            RectTransform header = holderRect.Find("HeaderBacker") as RectTransform;
            RectTransform buttons = holderRect.Find("ButtonHolder") as RectTransform;
            if (backing == null || header == null || buttons == null || !(target.transform is RectTransform)
                || target.m_Message.rectTransform.rect.height <= 0) return false;

            dialog = target;
            root = target.transform as RectTransform;
            holder = holderRect;
            message = target.m_Message;
            title = target.m_Title;
            messageState = new RectState(message.rectTransform);
            backingState = new RectState(backing);
            headerState = new RectState(header);
            buttonsState = new RectState(buttons);
            messageHeight = message.rectTransform.rect.height;
            backingHeight = backing.rect.height;

            Image image = backing.GetComponent<Image>();
            if (image != null && image.sprite != null && image.type == Image.Type.Simple && !image.preserveAspect)
            {
                Vector4 padding = UnityEngine.Sprites.DataUtility.GetPadding(image.sprite);
                panelBottomInset = padding.y / image.sprite.rect.height;
                panelTopInset = padding.w / image.sprite.rect.height;
                panelFill = Mathf.Max(0.1f, 1f - panelBottomInset - panelTopInset);
                background = backing.gameObject.AddComponent<FailureDialogBackground>();
                background.OriginalHeight = backingHeight * panelFill;
                background.enabled = false;
            }

            originalBestFit = message.resizeTextForBestFit;
            originalOverflow = message.verticalOverflow;
            originalAlignment = message.alignment;
            return root != null && messageHeight > 0;
        }

        private void Update()
        {
            if (!requested) return;
            try
            {
                // Layout work is limited to 5 Hz and only runs when text or geometry changes; the card's Animator moves
                // the holder every frame, so an unthrottled check would rebuild the layout continuously.
                if (Time.unscaledTime >= nextCheck)
                {
                    nextCheck = Time.unscaledTime + 0.2f;
                    if (lastText != message.text || lastRootSize != root.rect.size
                        || (lastPosition - holder.position).sqrMagnitude > 1f || lastScale != holder.lossyScale)
                    {
                        Refresh();
                    }
                }
            }
            catch (Exception ex)
            {
                requested = false;
                Restore();
                BlockerLog.Warning("Popup", "DIALOG_LAYOUT_UPDATE_FAILED", ex.ToString());
            }
        }

        private void Refresh()
        {
            bool changedText = lastText != message.text;
            Restore();
            message.resizeTextForBestFit = false;
            message.verticalOverflow = VerticalWrapMode.Overflow;
            FitTitle();

            float widthScale = Mathf.Abs(holder.lossyScale.x / root.lossyScale.x);
            float width = Mathf.Min(backingState.Rect.rect.width * 0.85f,
                root.rect.width * 0.8f / Mathf.Max(0.01f, widthScale));
            messageState.Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);

            float topInset = message.fontSize * message.lineSpacing * 0.5f;
            float contentHeight = Mathf.Ceil(message.preferredHeight) + 8f;
            float wanted = Mathf.Max(messageHeight, contentHeight + topInset);

            // The stock card lives inside a full-screen dialog root whose animated ContentHolder reports canvas-space
            // coordinates, so the original HostUtilities bound (root edges) is always negative here. Bound the growth
            // to a small fraction of the screen instead; the message only needs a few lines of extra height.
            float extra = Mathf.Min(wanted - messageHeight, Mathf.Max(0f, root.rect.height * 0.15f));
            float height = messageHeight + extra;

            float panelExtra = extra / panelFill;
            backingState.Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, backingHeight + panelExtra);
            backingState.Rect.anchoredPosition += Vector2.up * (panelExtra * (backingState.Rect.pivot.y - panelBottomInset) - extra * 0.5f);
            if (background != null) background.enabled = extra > 0;
            headerState.Rect.anchoredPosition += Vector2.up * (extra * 0.5f);
            buttonsState.Rect.anchoredPosition -= Vector2.up * (extra * 0.5f);

            messageState.Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height - topInset);
            messageState.Rect.anchoredPosition -= Vector2.up * (topInset * (1f - messageState.Rect.pivot.y));

            lastText = message.text;
            lastRootSize = root.rect.size;
            lastPosition = holder.position;
            lastScale = holder.lossyScale;
            BlockerLog.Debug("Popup", "DIALOG_LAYOUT", "root=" + root.rect.size.ToString("0") + " messageHeight=" + messageHeight.ToString("0") + " grew=" + extra.ToString("0")
                + "px; content=" + contentHeight.ToString("0") + " font=" + message.fontSize
                + (changedText ? " (new text)" : string.Empty));
        }

        // The localized title can be wider than the stock title rect. Shrinking only the title keeps the message at its
        // readable size while both stay on the card.
        private void FitTitle()
        {
            if (title == null) return;
            RectTransform titleRect = title.rectTransform;
            if (titleRect == null || titleRect.rect.width <= 1f) return;
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            title.verticalOverflow = VerticalWrapMode.Truncate;
            float needed = title.preferredWidth;
            int size = title.fontSize;
            if (needed > titleRect.rect.width && needed > 1f)
                title.fontSize = Mathf.Max(10, Mathf.FloorToInt(size * (titleRect.rect.width / needed)));
        }

        private void Restore()
        {
            if (message == null) return;
            if (background != null) background.enabled = false;
            if (messageState != null) messageState.Restore();
            if (backingState != null) backingState.Restore();
            if (headerState != null) headerState.Restore();
            if (buttonsState != null) buttonsState.Restore();
            message.resizeTextForBestFit = originalBestFit;
            message.verticalOverflow = originalOverflow;
            message.alignment = originalAlignment;
        }

        private void OnDisable()
        {
            if (!requested) return;
            requested = false;
            try { Restore(); }
            catch (Exception ex) { BlockerLog.Warning("Popup", "DIALOG_LAYOUT_RESTORE_FAILED", ex.ToString()); }
        }
    }

    // The native panel sprite has no nine-slice border; stretch only its middle so the visible white card grows with
    // the message while the original edge strips stay at their designed thickness.
    internal sealed class FailureDialogBackground : BaseMeshEffect
    {
        internal float OriginalHeight;
        private readonly UIVertex[] quad = new UIVertex[4];
        private readonly float[] source = { 0f, 0f, 0f, 1f };
        private readonly float[] target = { 0f, 0f, 0f, 1f };

        public override void ModifyMesh(VertexHelper mesh)
        {
            if (!IsActive() || mesh.currentVertCount != 4 || OriginalHeight <= 0f) return;
            try
            {
                UIVertex bottomLeft = new UIVertex(), topLeft = new UIVertex(), topRight = new UIVertex(), bottomRight = new UIVertex();
                mesh.PopulateUIVertex(ref bottomLeft, 0); mesh.PopulateUIVertex(ref topLeft, 1);
                mesh.PopulateUIVertex(ref topRight, 2); mesh.PopulateUIVertex(ref bottomRight, 3);
                float height = topLeft.position.y - bottomLeft.position.y;
                if (height <= OriginalHeight) return;
                float edge = Mathf.Min(128f, OriginalHeight * 0.45f);
                source[1] = edge / OriginalHeight; source[2] = 1f - source[1];
                target[1] = edge / height; target[2] = 1f - target[1];
                mesh.Clear();
                for (int i = 0; i < 3; i++)
                {
                    quad[0] = Vertex(bottomLeft, topLeft, target[i], source[i]);
                    quad[1] = Vertex(bottomLeft, topLeft, target[i + 1], source[i + 1]);
                    quad[2] = Vertex(bottomRight, topRight, target[i + 1], source[i + 1]);
                    quad[3] = Vertex(bottomRight, topRight, target[i], source[i]);
                    mesh.AddUIVertexQuad(quad);
                }
            }
            catch (Exception ex) { BlockerLog.Warning("Popup", "DIALOG_BACKGROUND_FAILED", ex.ToString()); }
        }

        private static UIVertex Vertex(UIVertex bottom, UIVertex top, float position, float texture)
        {
            UIVertex vertex = bottom;
            vertex.position = Vector3.Lerp(bottom.position, top.position, position);
            vertex.uv0 = Vector2.Lerp(bottom.uv0, top.uv0, texture);
            return vertex;
        }
    }
}
