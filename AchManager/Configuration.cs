using AchManager.AchievementTrigger;
using AchManager.EventManager;
using Dalamud.Configuration;
using Dalamud.Plugin;
using ECommons.DalamudServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace AchManager;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
  [Serializable]
  internal sealed class WatchedAchievementState
  {
    public AchievementUpdateTriggerBase? Trigger { get; set; }

    public uint? Progress { get; set; }

    public uint? ProgressMax { get; set; }

    public long? UpdatedAtUnix { get; set; }
  }

  public int Version { get; set; } = 0;

  public bool PreventChatEventManagerLogSpam { get; set; } = true;

  /// <summary>
  /// List of watched achievements.
  /// </summary>
  [JsonIgnore]
  public IEnumerable<WatchedAchievement> Achievements => _achievementManager?.Achievements ?? [];

  /// <summary>
  /// Holds the ids of all watched achievements together with the
  /// configured trigger type and last observed progress. Should not be modified directly.
  /// Use <see cref="AddWatchedAchievement(uint)"/> or <see cref="RemoveWatchedAchievement(uint)"/>.
  /// </summary>
  internal Dictionary<uint, WatchedAchievementState> WatchedAchievements { get; set; } = [];

  [NonSerialized]
  private IDalamudPluginInterface? PluginInterface;

  [NonSerialized]
  private WatchedAchievementManager? _achievementManager;

  [NonSerialized]
  private static readonly Dictionary<TriggerType, Type?> _availableTrigger = new()
  {
    { TriggerType.DutyCompleted, typeof(DutyCompletedTrigger) },
    { TriggerType.FateCompleted, typeof(FateCompletedTrigger) },
    { TriggerType.MarkKilled, typeof(MarkKilledTrigger) },
    { TriggerType.ChatMessage, typeof(ChatMessageTrigger) },
    { TriggerType.QuestCompleded, typeof(QuestCompletedTrigger) },
    { TriggerType.BannerShown, typeof(BannerShownTrigger) },
    { TriggerType.None, null }
  };

  [JsonIgnore]
  public static Dictionary<uint, (uint initial, uint current, uint max)> SessionStats { get; } = [];

  public void Initialize(IDalamudPluginInterface pluginInterface)
  {
    PluginInterface = pluginInterface;
    _achievementManager = new WatchedAchievementManager();
    _achievementManager.OnWatchedAchievementRemovalRequested += AchievementManager_OnWatchedAchievementRemovalRequested;
    InitializeManager();
  }

  public void Save()
  {
    var settings = new JsonSerializerSettings()
    {
      ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
    };

    var serializedThis = JsonConvert.SerializeObject(this, settings);
    File.WriteAllText(Path.Combine(PluginInterface!.ConfigDirectory.FullName, "AchManager.json"), serializedThis);

    try
    {
      var serialized = JsonConvert.SerializeObject(WatchedAchievements);
      File.WriteAllText(Path.Combine(PluginInterface!.ConfigDirectory.FullName, "AchManagerWA.json"), serialized);
    }
    catch (Exception ex)
    {
      Svc.Log.Error($"Error while serializing WatchedAchievements: {ex.Message}");
    }
  }

  public static Configuration? Load(string path)
  {
    try
    {
      var settings = new JsonSerializerSettings()
      {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
      };

      return JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(path), settings);
    }
    catch (Exception ex)
    {
      Svc.Log.Error($"Error while loading plugin configuration: {ex.Message}");
      return null;
    }
  }

  public void AddWatchedAchievement(uint id)
  {
    if (WatchedAchievements.TryAdd(id, new WatchedAchievementState()))
      _achievementManager!.AddWatchedAchievement(id, null);

    Save();
  }

  public void RemoveWatchedAchievement(uint id)
  {
    if (WatchedAchievements.Remove(id))
    {
      _achievementManager!.RemoveWatchedAchievement(id);
      Save();
    }
  }

  public void ChangeTriggerTypeForAchievement(uint id, TriggerType triggerType)
  {
    if (WatchedAchievements.TryGetValue(id, out WatchedAchievementState? watchedState))
    {
      var type = _availableTrigger[triggerType];
      var trigger = type == null ? null : (AchievementUpdateTriggerBase?)Activator.CreateInstance(type);
      watchedState.Trigger = trigger;
      _achievementManager!.SetTriggerTypeForWatchedAchievement(id, trigger);
      Save();
    }
  }

  internal void UpdateWatchedProgress(uint id, uint progress, uint progressMax)
  {
    if (!WatchedAchievements.TryGetValue(id, out WatchedAchievementState? watchedState))
      return;

    if (watchedState.Progress == progress && watchedState.ProgressMax == progressMax)
      return;

    watchedState.Progress = progress;
    watchedState.ProgressMax = progressMax;
    watchedState.UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    Save();
  }

  private void InitializeManager()
  {
    try
    {
      var file = Path.Combine(PluginInterface!.ConfigDirectory.FullName, "AchManagerWA.json");
      if (File.Exists(file))
        WatchedAchievements = DeserializeWatchedAchievements(File.ReadAllText(file));
    }
    catch (Exception ex)
    {
      Svc.Log.Error($"Error while deserializing WatchedAchievements: {ex.Message}");
    }

    foreach (var ach in WatchedAchievements)
      _achievementManager!.AddWatchedAchievement(ach.Key, ach.Value.Trigger);
  }

  private static Dictionary<uint, WatchedAchievementState> DeserializeWatchedAchievements(string json)
  {
    if (string.IsNullOrWhiteSpace(json))
      return [];

    JObject? root;
    try
    {
      root = JObject.Parse(json);
    }
    catch (Exception)
    {
      return [];
    }

    Dictionary<uint, WatchedAchievementState> result = [];
    foreach (var property in root.Properties())
    {
      if (!uint.TryParse(property.Name, out uint id))
        continue;

      WatchedAchievementState state = new();
      JToken value = property.Value;
      if (value.Type == JTokenType.Object)
      {
        JObject obj = (JObject)value;
        bool isStateFormat = obj.Property(nameof(WatchedAchievementState.Trigger), StringComparison.OrdinalIgnoreCase) != null;
        if (isStateFormat)
        {
          state = obj.ToObject<WatchedAchievementState>() ?? new WatchedAchievementState();
        }
        else
        {
          state.Trigger = obj.ToObject<AchievementUpdateTriggerBase?>();
        }
      }

      result[id] = state;
    }

    return result;
  }

  private void AchievementManager_OnWatchedAchievementRemovalRequested(object? sender, EventArgs e)
  {
    if (sender is WatchedAchievement ach)
    {
      RemoveWatchedAchievement(ach.WatchedID);
      Svc.Log.Info($"Achievement with ID {ach.WatchedID} removed from watchlist, due to it being completed.");
    }
  }
}
