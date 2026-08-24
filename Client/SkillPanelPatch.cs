using EFT;
using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SkillPointsMod.Client;

// Injects a "Level Up" spend button into each row of the real in-game
// skills list, underneath the skill's icon. SkillPanel.Show(Skill,
// IHealthController) runs once per row instantiation (EFT.UI.SkillContainer<T>
// clones a template per skill via AddViewListAsync), but AsyncViewList may
// pool/reuse row instances across re-sorts/re-opens, so this always rebinds
// which skill the existing button targets rather than assuming a row's
// skill never changes.
[HarmonyPatch(typeof(SkillPanel), nameof(SkillPanel.Show))]
internal static class SkillPanelPatch
{
    private const string ButtonName = "SkillPointsMod_SpendButton";

    [HarmonyPostfix]
    private static void Postfix(SkillPanel __instance, Skill skill)
    {
        SkillPointsUiController.RegisterLiveSkill(skill);

        var row = __instance.transform;
        var existing = row.Find(ButtonName);
        SkillPointsRowBinding binding;

        if (existing == null)
        {
            binding = CreateButton(row, __instance._level.font);
        }
        else
        {
            binding = existing.GetComponent<SkillPointsRowBinding>();
        }

        binding.SkillId = skill.Id;
        // Under the icon, not next to the name -- the name's row is where
        // buff icons render once a skill has any, which our button was
        // colliding with there.
        binding.AnchorRect = (RectTransform)__instance._skillIcon.transform;
    }

    private static SkillPointsRowBinding CreateButton(Transform row, TMP_FontAsset? font)
    {
        var buttonGo = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonGo.transform.SetParent(row, false);

        var rect = (RectTransform)buttonGo.transform;
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(72f, 16f);
        // anchoredPosition/anchors deliberately left at default here --
        // SkillPointsRowBinding.Update() repositions via world-space
        // .position every frame instead.

        // The row prefab (or an ancestor near it) arranges children via a
        // LayoutGroup -- without ignoreLayout, adding this as a plain child
        // gets included in that arrangement and squeezes/reflows the whole
        // visible list. Applied defensively to every injected root in this
        // mod now (see also SkillsScreenPatch), since which specific
        // ancestor is unsafe wasn't fully pinned down across three attempts.
        var layoutElement = buttonGo.AddComponent<LayoutElement>();
        layoutElement.ignoreLayout = true;

        var image = buttonGo.GetComponent<Image>();
        image.color = new Color(0.2f, 0.6f, 0.2f, 0.85f);

        var button = buttonGo.GetComponent<Button>();
        button.targetGraphic = image;

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(buttonGo.transform, false);
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var label = labelGo.AddComponent<TextMeshProUGUI>();
        label.text = "Level Up";
        label.fontSize = 10f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        if (font != null)
        {
            label.font = font;
        }

        var binding = buttonGo.AddComponent<SkillPointsRowBinding>();
        binding.Button = button;
        binding.Image = image;
        binding.Label = label;
        binding.Rect = rect;

        button.onClick.AddListener(() => SkillPointsUiController.TrySpend(binding.SkillId));

        return binding;
    }
}

internal class SkillPointsRowBinding : MonoBehaviour
{
    public Button Button = null!;
    public Image Image = null!;
    public TextMeshProUGUI Label = null!;
    public RectTransform Rect = null!;
    public RectTransform AnchorRect = null!;
    public ESkillId SkillId;

    private static readonly Vector3[] _corners = new Vector3[4];

    private void Update()
    {
        if (AnchorRect != null)
        {
            AnchorRect.GetWorldCorners(_corners); // 0=BL,1=TL,2=TR,3=BR
            var bottomMid = (_corners[0] + _corners[3]) / 2f;
            Rect.position = bottomMid + new Vector3(0f, -2f, 0f);
        }

        var atCap = SkillPointsUiController.IsSkillCapped(SkillId);
        if (atCap)
        {
            SetVisible(true);
            Button.interactable = false;
            Label.text = "MAX";
            return;
        }

        // Nothing useful this button can do right now -- hide it rather
        // than just greying it out. Deliberately NOT GameObject.SetActive:
        // that would disable this very component too, and Unity stops
        // calling Update() on components of an inactive GameObject -- once
        // hidden that way, it could never re-check whether it should show
        // again (confirmed: buttons stayed hidden even after a refund
        // brought the balance back above zero). Toggling the renderable
        // components instead keeps this Update() polling regardless.
        var canSpend = SkillPointsUiController.UnspentPoints >= 1;
        SetVisible(canSpend);
        if (canSpend)
        {
            Button.interactable = true;
            Label.text = "Level Up";
        }
    }

    private void SetVisible(bool visible)
    {
        Image.enabled = visible;
        Label.enabled = visible;
        Button.enabled = visible;
    }
}
