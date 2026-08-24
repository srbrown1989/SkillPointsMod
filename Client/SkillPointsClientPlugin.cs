using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace SkillPointsMod.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class SkillPointsClientPlugin : BaseUnityPlugin
{
    private const string PluginGuid = "com.imperator.skillpointsmod.client";
    private const string PluginName = "SkillPointsMod Client";
    private const string PluginVersion = "0.1.0";

    private const double MaxSkillProgress = 5100d; // mirrors CommonSkill.MaxSkillProgress server-side

    // ESkillId includes BotReload/BotSound -- present in a real profile's
    // Skills.Common (verified against the server's profiles.json template)
    // but meaningless to the player, so they're left off the overlay.
    private static readonly ESkillId[] DisplaySkills = Enum.GetValues(typeof(ESkillId))
        .Cast<ESkillId>()
        .Where(skill => skill != ESkillId.BotReload && skill != ESkillId.BotSound)
        .ToArray();

    private ConfigEntry<KeyCode> _toggleKey = null!;
    private bool _visible;
    private Rect _windowRect = new(20, 20, 320, 460);
    private Vector2 _scrollPosition;

    private void Awake()
    {
        _toggleKey = Config.Bind(
            "General",
            "ToggleKey",
            KeyCode.F9,
            "Key to show/hide the Skill Points overlay (a fallback alongside the buttons now embedded " +
            "directly in the real skills screen)."
        );

        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
    }

    private void Update()
    {
        if (!Input.GetKeyDown(_toggleKey.Value))
        {
            return;
        }

        _visible = !_visible;
        if (_visible)
        {
            SkillPointsUiController.RefreshStatus();
        }
    }

    private void OnGUI()
    {
        if (!_visible)
        {
            return;
        }

        _windowRect = GUILayout.Window(GetInstanceID(), _windowRect, DrawWindow, "Skill Points");
    }

    private void DrawWindow(int windowId)
    {
        GUILayout.Label($"Unspent points: {SkillPointsUiController.UnspentPoints:0.##}");

        if (SkillPointsUiController.LastError is { } error)
        {
            GUILayout.Label(error);
        }

        GUILayout.Space(6);

        _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(320));
        foreach (var skill in DisplaySkills)
        {
            var atCap = SkillPointsUiController.SkillProgress.TryGetValue(skill.ToString(), out var progress)
                && progress >= MaxSkillProgress;

            GUILayout.BeginHorizontal();
            GUILayout.Label(skill.ToString(), GUILayout.Width(160));

            GUI.enabled = !atCap && SkillPointsUiController.UnspentPoints >= 1;
            if (GUILayout.Button(atCap ? "MAX" : "+1", GUILayout.Width(40)))
            {
                SkillPointsUiController.TrySpend(skill);
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        GUILayout.Space(6);

        if (GUILayout.Button("Reset all skills"))
        {
            SkillPointsUiController.TryReset();
        }

        if (GUILayout.Button("Refresh"))
        {
            SkillPointsUiController.RefreshStatus();
        }

        GUI.DragWindow();
    }
}
