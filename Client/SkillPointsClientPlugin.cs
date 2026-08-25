using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace SkillPointsMod.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class SkillPointsClientPlugin : BaseUnityPlugin
{
    private const string PluginGuid = "com.imperator.skillpointsmod.client";
    private const string PluginName = "SkillPointsMod Client";
    private const string PluginVersion = "0.1.0";

    private void Awake()
    {
        // Settings are edited live via BepInEx's Configuration Manager
        // (F12, already installed) instead of a custom overlay -- every
        // ConfigEntry bound in SkillXpSettings.Initialize is auto-discovered
        // by it, and per-skill entries tag themselves with IsAdvanced so the
        // main/advanced split shows up under Configuration Manager's own
        // built-in "Show advanced" toggle.
        SkillXpSettings.Initialize(Config);

        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
    }

    private void Update()
    {
        // Applied every frame regardless of anything else -- fatigue
        // settings should be in effect whenever the game is running.
        SkillXpSettings.ApplyFatigueSettings();
    }
}
