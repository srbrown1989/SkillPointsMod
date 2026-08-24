using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SkillPointsMod.Client;

// Injects an unspent-points label + reset button into the real skills
// screen's fixed top toolbar (next to the type/sort dropdowns, which don't
// scroll with the list), plus a confirmation dialog for the reset action.
// SkillsScreen.Show(Profile, IHealthController) is the per-tab-open entry
// point (SkillsAndMasteringScreen also covers the unrelated Mastering tab,
// so this patches the more specific Skills one).
[HarmonyPatch(typeof(SkillsScreen), nameof(SkillsScreen.Show))]
internal static class SkillsScreenPatch
{
    private const string HeaderName = "SkillPointsMod_Header";
    private const string ConfirmOverlayName = "SkillPointsMod_ResetConfirm";

    [HarmonyPostfix]
    private static void Postfix(SkillsScreen __instance)
    {
        // Both built under the SkillsScreen's own root, NOT under
        // _skillList's parent -- that parent turned out to be inside a
        // LayoutGroup/ContentSizeFitter (confirmed: exempting the header
        // from it via ignoreLayout fixed the previously-squashed skill list,
        // but then the header itself stopped rendering, implying that same
        // container was also what gave it a valid size/visibility). The
        // screen root has no such issue -- it's already proven to work for
        // the confirm dialog.
        var screenRoot = __instance.transform;
        var confirmOverlay = screenRoot.Find(ConfirmOverlayName);
        if (confirmOverlay == null)
        {
            confirmOverlay = BuildConfirmDialog(screenRoot);
        }

        // Anchored to _filterMethod (part of the fixed top toolbar, not the
        // scrollable list) rather than _skillList -- positioning off the
        // list meant the header visually sat between the fixed header and
        // the scrolling content, and moved when the list scrolled.
        var filterRect = (RectTransform)__instance._filterMethod.transform;
        var header = screenRoot.Find(HeaderName);
        if (header == null)
        {
            header = BuildHeader(screenRoot, confirmOverlay.gameObject);
        }
        header.GetComponent<SkillPointsHeaderBinding>().AnchorRect = filterRect;

        SkillPointsUiController.RefreshStatus();
    }

    private static Transform BuildHeader(Transform screenRoot, GameObject confirmOverlay)
    {
        var headerGo = new GameObject(HeaderName, typeof(RectTransform));
        headerGo.transform.SetParent(screenRoot, false);
        headerGo.AddComponent<LayoutElement>().ignoreLayout = true;

        var rect = (RectTransform)headerGo.transform;
        rect.pivot = new Vector2(1f, 0.5f); // hangs to the LEFT of the anchor point
        rect.sizeDelta = new Vector2(300f, 28f);
        // anchoredPosition/anchors deliberately left at default -- position
        // is set every frame in SkillPointsHeaderBinding.Update() from
        // _filterMethod's own live world-space corners, the same proven
        // approach as the row buttons in SkillPanelPatch.

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(headerGo.transform, false);
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(0.65f, 1f);
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var label = labelGo.AddComponent<TextMeshProUGUI>();
        label.fontSize = 16f;
        label.alignment = TextAlignmentOptions.Midline;
        label.color = Color.white;

        var resetButton = CreateSmallButton(
            headerGo.transform,
            "Reset",
            new Color(0.6f, 0.2f, 0.2f, 0.85f),
            new Vector2(1f, 0.5f),
            new Vector2(1f, 0.5f),
            new Vector2(1f, 0.5f),
            Vector2.zero,
            new Vector2(90f, 28f)
        );

        var confirmBinding = confirmOverlay.GetComponent<SkillPointsResetConfirmBinding>();
        resetButton.onClick.AddListener(confirmBinding.Open);

        var binding = headerGo.AddComponent<SkillPointsHeaderBinding>();
        binding.Label = label;
        binding.Rect = rect;

        return headerGo.transform;
    }

    private static Transform BuildConfirmDialog(Transform screenRoot)
    {
        var overlayGo = new GameObject(ConfirmOverlayName, typeof(RectTransform), typeof(Image));
        overlayGo.transform.SetParent(screenRoot, false);
        overlayGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var overlayRect = (RectTransform)overlayGo.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        overlayGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);

        var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image));
        boxGo.transform.SetParent(overlayGo.transform, false);
        var boxRect = (RectTransform)boxGo.transform;
        boxRect.anchorMin = new Vector2(0.5f, 0.5f);
        boxRect.anchorMax = new Vector2(0.5f, 0.5f);
        boxRect.pivot = new Vector2(0.5f, 0.5f);
        boxRect.sizeDelta = new Vector2(440f, 180f);
        boxGo.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f, 0.98f);

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(boxGo.transform, false);
        var textRect = (RectTransform)textGo.transform;
        textRect.anchorMin = new Vector2(0f, 0.35f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.offsetMin = new Vector2(16f, 0f);
        textRect.offsetMax = new Vector2(-16f, -16f);

        var text = textGo.AddComponent<TextMeshProUGUI>();
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 18f;
        text.color = Color.white;

        var confirmButton = CreateSmallButton(
            boxGo.transform,
            "Confirm",
            new Color(0.2f, 0.5f, 0.2f, 0.9f),
            new Vector2(0.28f, 0.15f),
            new Vector2(0.28f, 0.15f),
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(140f, 36f)
        );
        var cancelButton = CreateSmallButton(
            boxGo.transform,
            "Cancel",
            new Color(0.4f, 0.4f, 0.4f, 0.9f),
            new Vector2(0.72f, 0.15f),
            new Vector2(0.72f, 0.15f),
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(140f, 36f)
        );

        overlayGo.SetActive(false);

        var binding = overlayGo.AddComponent<SkillPointsResetConfirmBinding>();
        binding.Text = text;
        binding.Overlay = overlayGo;

        confirmButton.onClick.AddListener(() =>
        {
            overlayGo.SetActive(false);
            SkillPointsUiController.TryReset();
        });
        cancelButton.onClick.AddListener(() => overlayGo.SetActive(false));

        return overlayGo.transform;
    }

    private static Button CreateSmallButton(
        Transform parent,
        string text,
        Color color,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPosition,
        Vector2 sizeDelta
    )
    {
        var go = new GameObject(text + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;

        var image = go.GetComponent<Image>();
        image.color = color;
        var button = go.GetComponent<Button>();
        button.targetGraphic = image;

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var label = labelGo.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = 14f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;

        return button;
    }
}

internal class SkillPointsHeaderBinding : MonoBehaviour
{
    public TextMeshProUGUI Label = null!;
    public RectTransform Rect = null!;
    public RectTransform? AnchorRect;

    private static readonly Vector3[] _corners = new Vector3[4];

    private void Update()
    {
        if (AnchorRect != null)
        {
            AnchorRect.GetWorldCorners(_corners); // 0=BL,1=TL,2=TR,3=BR
            var leftMid = (_corners[0] + _corners[1]) / 2f;
            // -150, not -12 -- there's a "Show:" label between the dropdown
            // and our box that we have no direct element reference to (it's
            // not one of SkillsScreen's exposed fields), which the smaller
            // gap wasn't clearing (confirmed via screenshot: our Reset
            // button was covering most of "Show:", "w:" poking out past it).
            Rect.position = leftMid + new Vector3(-150f, 0f, 0f);
        }

        var error = SkillPointsUiController.LastError;
        Label.text = error ?? $"Unspent skill points: {SkillPointsUiController.UnspentPoints:0.##}";
    }
}

internal class SkillPointsResetConfirmBinding : MonoBehaviour
{
    public TextMeshProUGUI Text = null!;
    public GameObject Overlay = null!;

    public void Open()
    {
        Text.text = SkillPointsUiController.ResetIsFree
            ? "Reset all spent skill points?\n(free -- debug mode)"
            : $"Reset all spent skill points?\nCosts {SkillPointsUiController.ResetCostAmount:N0} {SkillPointsUiController.ResetCostCurrency}.";
        Overlay.SetActive(true);
    }
}
