using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ImGuiCond = Dalamud.Bindings.ImGui.ImGuiCond;
using ImGuiWindowFlags = Dalamud.Bindings.ImGui.ImGuiWindowFlags;

namespace Forest.Windows
{
    public class ForestWindow : Window, IDisposable
    {
        private readonly IClientState clientState;
        private readonly IObjectTable objectTable;
        private readonly IFramework framework;
        private readonly ForestConfig config;
        private readonly IDalamudPluginInterface pluginInterface;
        private readonly IChatGui chatGui;

        private string[] players = Array.Empty<string>();
        private int selectedPlayerIndex = -1;
        private PlayerData? currentPlayerData;

        private enum ViewMode { Murder, Hunt, MurderMystery }
        private ViewMode currentView = ViewMode.Murder;

        private DateTime lastRefreshTime = DateTime.MinValue;
        private readonly DateTime pluginStartTime;

        // Voting period timer
        private DateTime? votingStartTime = null;
        private readonly TimeSpan votingDuration = TimeSpan.FromMinutes(5);

        // Whisper tracking
        private readonly Dictionary<string, string> receivedWhispers = new();
        private readonly HashSet<string> whispersProcessed = new();

        public ForestWindow(
            IClientState clientState,
            IObjectTable objectTable,
            IFramework framework,
            ForestConfig config,
            IDalamudPluginInterface pluginInterface,
            IChatGui chatGui)
            : base("MiniGame Window##WithHiddenID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.MenuBar)
        {
            this.clientState = clientState;
            this.objectTable = objectTable;
            this.framework = framework;
            this.config = config;
            this.pluginInterface = pluginInterface;
            this.chatGui = chatGui;
            this.pluginStartTime = DateTime.UtcNow;

            // Debug: Log what we loaded
            Plugin.Log.Information($"ForestWindow initialized:");
            Plugin.Log.Information($"  - Loaded games: {config.MurderMysteryGames.Count}");
            Plugin.Log.Information($"  - Current game: {config.CurrentGame?.Title ?? "None"}");
            Plugin.Log.Information($"  - Player database: {config.PlayerDatabase.Count}");

            this.framework.Update += OnFrameworkUpdate;
            this.chatGui.ChatMessage += OnChatMessage;

            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(375, 430),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
        }

        public void Dispose()
        {
            this.framework.Update -= OnFrameworkUpdate;
            this.chatGui.ChatMessage -= OnChatMessage;
        }

        private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            // Only process incoming whispers during voting period
            if (type != XivChatType.TellIncoming || !votingStartTime.HasValue || config.CurrentGame == null)
                return;

            string senderName = sender.TextValue;
            string messageText = message.TextValue;

            // Check if sender is an active player (excluding the killer)
            if (!config.CurrentGame.ActivePlayers.Contains(senderName) || senderName == config.CurrentGame.Killer)
                return;

            // Check if we already have a whisper from this player for this voting round
            if (receivedWhispers.ContainsKey(senderName))
                return;

            // Store the whisper
            receivedWhispers[senderName] = messageText;

            // Log for debugging
            Plugin.Log.Information($"[Murder Mystery] Captured whisper from {senderName}: {messageText}");
        }

        private void OnFrameworkUpdate(IFramework _)
        {
            if ((DateTime.UtcNow - pluginStartTime).TotalSeconds < 5)
                return;

            if (!clientState.IsLoggedIn || clientState.LocalPlayer == null)
                return;

            CheckVotingPeriod();
            CheckCountdownCompletion();

            if ((DateTime.UtcNow - lastRefreshTime).TotalSeconds < 10)
                return;

            lastRefreshTime = DateTime.UtcNow;

            var nearbyPlayers = objectTable
                .Where(o => o is IPlayerCharacter)
                .Cast<IPlayerCharacter>()
                .Select(pc => pc.Name.TextValue)
                .Distinct()
                .OrderBy(name => name)
                .ToArray();

            players = nearbyPlayers;
        }

        public override void Draw()
        {
            ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);

            // Menu bar
            if (ImGui.BeginMenuBar())
            {
                if (ImGui.Button("Hunt"))
                {
                    currentView = ViewMode.Hunt;
                }

                if (ImGui.Button("Murder Mystery"))
                    currentView = ViewMode.MurderMystery;

                ImGui.EndMenuBar();
            }

            ImGui.Columns(2, "MainColumns", true);
            ImGui.SetColumnWidth(0, 200);

            // Left column with player list and game list
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.12f, 0.12f, 1.0f));

            // Player list (reduced height to make room for game list)
            float playerListHeight = ImGui.GetContentRegionAvail().Y * 0.6f;
            ImGui.BeginChild("PlayerList", new Vector2(200, playerListHeight), true, ImGuiWindowFlags.HorizontalScrollbar);

            if (players.Length > 0)
            {
                for (int i = 0; i < players.Length; i++)
                {
                    bool isSelected = (i == selectedPlayerIndex);
                    string playerName = players[i];
                    bool isActivePlayer = config.CurrentGame?.ActivePlayers.Contains(playerName) ?? false;
                    bool isDead = config.CurrentGame?.DeadPlayers.Contains(playerName) ?? false;
                    bool isImprisoned = config.CurrentGame?.ImprisonedPlayers.Contains(playerName) ?? false;

                    Vector4 textColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f); // Default white
                    string statusSuffix = "";

                    if (isDead)
                    {
                        textColor = new Vector4(1.0f, 0.2f, 0.2f, 1.0f); // Red
                        statusSuffix = " (Dead)";
                    }
                    else if (isImprisoned)
                    {
                        textColor = new Vector4(0.2f, 0.2f, 1.0f, 1.0f); // Blue
                        statusSuffix = " (Imprisoned)";
                    }
                    else if (isActivePlayer)
                    {
                        textColor = new Vector4(0.2f, 1.0f, 0.2f, 1.0f); // Green
                        statusSuffix = " (Active)";
                    }

                    ImGui.PushStyleColor(ImGuiCol.Text, textColor);
                    if (ImGui.Selectable($"{playerName}{statusSuffix}", isSelected))
                    {
                        HandlePlayerSelection(i);
                    }
                    ImGui.PopStyleColor();

                    // Context menu for player actions
                    if (ImGui.BeginPopupContextItem($"PlayerContext_{i}"))
                    {
                        if (config.CurrentGame != null)
                        {
                            if (ImGui.MenuItem("Add to Murder Mystery") && !config.CurrentGame.ActivePlayers.Contains(playerName))
                            {
                                config.CurrentGame.ActivePlayers.Add(playerName);
                                SaveConfig();
                            }

                            if (ImGui.MenuItem("Mark as Dead") && !isDead)
                            {
                                config.CurrentGame.DeadPlayers.Add(playerName);
                                config.CurrentGame.ImprisonedPlayers.Remove(playerName); // Can't be both
                                SaveConfig();
                            }

                            if (ImGui.MenuItem("Mark as Imprisoned") && !isImprisoned)
                            {
                                config.CurrentGame.ImprisonedPlayers.Add(playerName);
                                config.CurrentGame.DeadPlayers.Remove(playerName); // Can't be both
                                SaveConfig();
                            }

                            if (ImGui.MenuItem("Remove Status"))
                            {
                                config.CurrentGame.DeadPlayers.Remove(playerName);
                                config.CurrentGame.ImprisonedPlayers.Remove(playerName);
                                SaveConfig();
                            }
                        }
                        ImGui.EndPopup();
                    }
                }
            }
            else
            {
                ImGui.TextDisabled("No players nearby.");
            }

            ImGui.EndChild();

            // Game list at bottom left
            ImGui.Spacing();
            ImGui.Text("Murder Mystery Games:");
            float gameListHeight = ImGui.GetContentRegionAvail().Y - 40;
            ImGui.BeginChild("GameList", new Vector2(200, gameListHeight), true);

            for (int i = 0; i < config.MurderMysteryGames.Count; i++)
            {
                var game = config.MurderMysteryGames[i];
                bool isCurrent = config.CurrentGame == game;

                if (isCurrent)
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 1.0f, 0.0f, 1.0f));

                string displayName = string.IsNullOrEmpty(game.Title) ? $"Game {i + 1}" : game.Title;
                if (ImGui.Selectable(displayName, isCurrent))
                {
                    config.CurrentGame = game;
                    currentView = ViewMode.MurderMystery;
                    SaveConfig();
                }

                if (isCurrent)
                    ImGui.PopStyleColor();
            }

            ImGui.EndChild();

            // + and - buttons for games
            if (ImGui.Button("+"))
            {
                var newGame = new MurderMysteryData { Title = $"New Game {config.MurderMysteryGames.Count + 1}" };
                config.MurderMysteryGames.Add(newGame);
                config.CurrentGame = newGame;

                // Debug logging
                Plugin.Log.Information($"Created new game: {newGame.Title}");
                Plugin.Log.Information($"Total games now: {config.MurderMysteryGames.Count}");
                Plugin.Log.Information($"Current game set to: {config.CurrentGame?.Title}");
                Plugin.Log.Information($"Current game index: {config.CurrentGameIndex}");

                SaveConfig();
                chatGui.Print($"[Debug] Created game '{newGame.Title}', total: {config.MurderMysteryGames.Count}");
            }
            ImGui.SameLine();
            if (ImGui.Button("-") && config.CurrentGame != null)
            {
                config.MurderMysteryGames.Remove(config.CurrentGame);
                config.CurrentGame = config.MurderMysteryGames.FirstOrDefault();
                SaveConfig();
            }

            // Debug button - remove this after testing
            //if (ImGui.Button("Reset All"))
            //{
            //    config.MurderMysteryGames.Clear();
            //    config.CurrentGame = null;
            //    config.PlayerDatabase.Clear();
            //    SaveConfig();
            //    chatGui.Print("[Debug] Reset all data");
            //}

            ImGui.PopStyleColor();

            ImGui.NextColumn();

            // Right column content
            ImGui.BeginChild("PlayerDetails", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar);

            if (currentView == ViewMode.MurderMystery)
            {
                DrawMurderMysteryView();
            }
            else if (currentPlayerData != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.9f, 1.0f, 1.0f));
                ImGui.Text($"Selected Player: {currentPlayerData.Name}");
                ImGui.PopStyleColor();

                ImGui.Separator();
                ImGui.Spacing();

                if (currentView == ViewMode.Murder)
                    DrawMurderView();
                else if (currentView == ViewMode.Hunt)
                    DrawHuntView();
            }
            else if (currentView == ViewMode.Murder || currentView == ViewMode.Hunt)
            {
                ImGui.Text("Select a player to view details.");
            }

            ImGui.EndChild();
        }

        private void HandlePlayerSelection(int playerIndex)
        {
            selectedPlayerIndex = playerIndex;

            // If in murder mystery view, switch to murder view and load player
            if (currentView == ViewMode.MurderMystery)
            {
                currentView = ViewMode.Murder;
            }

            LoadOrCreatePlayerData(players[playerIndex]);
        }

        private void DrawMurderView()
        {
            // Calculate required whisper fields based on active players + 1 round
            int requiredWhisperFields = GetRequiredWhisperFields();
            int totalRounds = requiredWhisperFields + 1; // +1 round as requested

            ImGui.Text($"Whisper Fields (Required: {requiredWhisperFields}, Total Rounds: {totalRounds}):");
            ImGui.Separator();

            // Draw whisper fields dynamically - only show the required amount
            for (int i = 0; i < requiredWhisperFields; i++)
            {
                string whisper = currentPlayerData.GetWhisper(i);

                if (ImGui.InputText($"Round {i + 1} Whisper", ref whisper, 256))
                {
                    currentPlayerData.SetWhisper(i, whisper);
                    SaveConfig();
                }
            }

            // Show final round indicator (no whisper field)
            if (totalRounds > requiredWhisperFields)
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), $"Round {totalRounds}: Final Round (No whispers)");
            }

            ImGui.Separator();
            ImGui.Text("Notes:");
            string notes = currentPlayerData.Notes;
            Vector2 notesSize = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 5);
            if (ImGui.InputTextMultiline("##notes", ref notes, 1024, notesSize, ImGuiInputTextFlags.AllowTabInput))
            {
                currentPlayerData.Notes = notes;
                SaveConfig();
            }
        }

        private int GetRequiredWhisperFields()
        {
            if (config.CurrentGame == null) return 0; // Start with 0 instead of 3

            int activePlayers = config.CurrentGame.ActivePlayers.Count;
            if (activePlayers == 0) return 0; // No players, no whispers needed

            int killer = string.IsNullOrEmpty(config.CurrentGame.Killer) ? 0 : 1;
            int livingPlayers = activePlayers - killer - config.CurrentGame.DeadPlayers.Count - config.CurrentGame.ImprisonedPlayers.Count;

            // Return the number of whisper rounds needed (living players / 2, minimum 1 if there are living players)
            return livingPlayers > 0 ? Math.Max(1, livingPlayers / 2) : 0;
        }

        private void DrawHuntView()
        {
            ImGui.Text("Hunt details would go here...");
        }

        private void DrawMurderMysteryView()
        {
            if (config.CurrentGame == null)
            {
                ImGui.Text("No murder mystery game selected. Create one using the + button.");
                return;
            }

            var mysteryData = config.CurrentGame;

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.8f, 0.2f, 1.0f));
            ImGui.Text("Murder Mystery Details");
            ImGui.PopStyleColor();
            ImGui.Separator();
            ImGui.Spacing();

            // Title
            string title = mysteryData.Title;
            if (ImGui.InputText("Title", ref title, 256))
            {
                mysteryData.Title = title;
                SaveConfig();
            }

            // Description
            ImGui.Text("Description:");
            string description = mysteryData.Description;
            Vector2 descSize = new Vector2(ImGui.GetContentRegionAvail().X, 80);
            if (ImGui.InputTextMultiline("##description", ref description, 1024, descSize, ImGuiInputTextFlags.AllowTabInput))
            {
                mysteryData.Description = description;
                SaveConfig();
            }

            ImGui.Spacing();

            // Voting Period
            ImGui.Separator();
            ImGui.Text("Voting Period:");

            if (votingStartTime.HasValue)
            {
                var remaining = votingDuration - (DateTime.UtcNow - votingStartTime.Value);
                if (remaining.TotalSeconds > 0)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.0f, 1.0f), $"Voting active: {remaining:mm\\:ss} remaining");
                    ImGui.SameLine();
                    if (ImGui.Button("Stop Voting"))
                    {
                        ProcessVotingResults();
                        votingStartTime = null;
                        SaveConfig();
                    }

                    // Show received whispers during voting
                    if (receivedWhispers.Count > 0)
                    {
                        ImGui.Text("Received Whispers:");
                        ImGui.Indent();
                        foreach (var kvp in receivedWhispers)
                        {
                            ImGui.TextColored(new Vector4(0.8f, 1.0f, 0.8f, 1.0f), $"{kvp.Key}: {kvp.Value}");
                        }
                        ImGui.Unindent();
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.2f, 0.2f, 1.0f), "Voting period ended");
                    ImGui.SameLine();
                    if (ImGui.Button("Reset"))
                    {
                        ProcessVotingResults();
                        votingStartTime = null;
                        SaveConfig();
                    }
                }
            }
            else
            {
                if (ImGui.Button("Start 5-Minute Voting"))
                {
                    StartVotingPeriod();
                }
            }

            ImGui.Spacing();

            // Active Players (clickable to set as killer)
            ImGui.Text("Active Players (click to set as killer):");
            ImGui.Indent();
            for (int i = mysteryData.ActivePlayers.Count - 1; i >= 0; i--)
            {
                string playerName = mysteryData.ActivePlayers[i];
                bool isKiller = mysteryData.Killer == playerName;
                bool isDead = mysteryData.DeadPlayers.Contains(playerName);
                bool isImprisoned = mysteryData.ImprisonedPlayers.Contains(playerName);

                Vector4 textColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
                if (isDead) textColor = new Vector4(1.0f, 0.2f, 0.2f, 1.0f);
                else if (isImprisoned) textColor = new Vector4(0.2f, 0.2f, 1.0f, 1.0f);
                else if (isKiller) textColor = new Vector4(1.0f, 0.2f, 0.2f, 1.0f);

                ImGui.PushStyleColor(ImGuiCol.Text, textColor);

                string displayName = $"• {playerName}";
                if (isDead) displayName += " (Dead)";
                else if (isImprisoned) displayName += " (Imprisoned)";
                else if (isKiller) displayName += " (Killer)";

                if (ImGui.Selectable(displayName, isKiller))
                {
                    mysteryData.Killer = playerName;
                    SaveConfig();
                }

                ImGui.PopStyleColor();

                ImGui.SameLine();
                ImGui.PushID(i);
                if (ImGui.SmallButton("Remove"))
                {
                    mysteryData.ActivePlayers.RemoveAt(i);
                    mysteryData.DeadPlayers.Remove(playerName);
                    mysteryData.ImprisonedPlayers.Remove(playerName);
                    if (mysteryData.Killer == playerName)
                        mysteryData.Killer = "";
                    SaveConfig();
                }
                ImGui.PopID();
            }
            ImGui.Unindent();

            if (mysteryData.ActivePlayers.Count == 0)
                ImGui.TextDisabled("No active players. Right-click players in the list to add them.");

            ImGui.Spacing();

            // Show required whisper fields
            int requiredFields = GetRequiredWhisperFields();
            int totalRoundsMM = requiredFields + 1;
            ImGui.Text($"Required Whisper Fields: {requiredFields}");
            ImGui.Text($"Total Rounds: {totalRoundsMM}");
            ImGui.Text($"Living Players: {mysteryData.ActivePlayers.Count - mysteryData.DeadPlayers.Count - mysteryData.ImprisonedPlayers.Count - (string.IsNullOrEmpty(mysteryData.Killer) ? 0 : 1)}");

            ImGui.Spacing();

            // Killer (now also shows selected from active players)
            string killer = mysteryData.Killer;
            if (ImGui.InputText("Killer", ref killer, 256))
            {
                mysteryData.Killer = killer;
                SaveConfig();
            }

            // Prize
            string prize = mysteryData.Prize;
            if (ImGui.InputText("Prize", ref prize, 256))
            {
                mysteryData.Prize = prize;
                SaveConfig();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text("Hint Timers (format: minutes:seconds):");

            // Dynamic hint timers - one for each round
            int totalRounds = requiredFields + 1;
            for (int i = 0; i < totalRounds; i++)
            {
                DrawCountdownHint($"Hint {i + 1} Timer", mysteryData, i);
            }
        }

        private void StartVotingPeriod()
        {
            votingStartTime = DateTime.UtcNow;
            receivedWhispers.Clear();
            whispersProcessed.Clear();
            chatGui.Print("[Murder Mystery] Voting period started! Send your votes via whisper.");
            SaveConfig();
        }

        private void ProcessVotingResults()
        {
            if (config.CurrentGame == null || receivedWhispers.Count == 0)
                return;

            // Assign whispers to the first available whisper slots for each player
            foreach (var kvp in receivedWhispers)
            {
                string playerName = kvp.Key;
                string whisperText = kvp.Value;

                // Get or create player data
                if (!config.PlayerDatabase.TryGetValue(playerName, out var playerData))
                {
                    playerData = new PlayerData { Name = playerName };
                    config.PlayerDatabase[playerName] = playerData;
                }

                // Find first empty whisper slot
                int whisperSlot = 0;
                while (!string.IsNullOrEmpty(playerData.GetWhisper(whisperSlot)))
                    whisperSlot++;

                playerData.SetWhisper(whisperSlot, whisperText);
            }

            chatGui.Print($"[Murder Mystery] Processed {receivedWhispers.Count} whispers from voting period.");
            receivedWhispers.Clear();
        }

        private void DrawCountdownHint(string label, MurderMysteryData mysteryData, int timerIndex)
        {
            var time = mysteryData.GetHintTime(timerIndex);
            var text = mysteryData.GetHintText(timerIndex);
            var endTime = mysteryData.GetTimerEndTime(timerIndex);
            var notified = mysteryData.GetTimerNotified(timerIndex);

            ImGui.Text(label);
            ImGui.Indent();

            // Time input
            ImGui.SetNextItemWidth(120);
            string timeStr = time;
            if (ImGui.InputText($"Time##{label}", ref timeStr, 32))
            {
                mysteryData.SetHintTime(timerIndex, timeStr);
                SaveConfig();
            }

            ImGui.SameLine();

            // Timer controls
            bool isRunning = endTime > DateTime.UtcNow;

            if (isRunning)
            {
                if (ImGui.Button($"Stop##{label}"))
                {
                    mysteryData.SetTimerEndTime(timerIndex, DateTime.MinValue);
                    SaveConfig();
                }

                ImGui.SameLine();
                var remaining = endTime - DateTime.UtcNow;
                if (remaining.TotalSeconds > 0)
                    ImGui.Text($"({remaining.Minutes:D2}:{remaining.Seconds:D2})");
                else
                    ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "(FINISHED)");
            }
            else
            {
                if (ImGui.Button($"Start##{label}"))
                {
                    if (ParseTimeString(timeStr, out int minutes, out int seconds))
                    {
                        var newEndTime = DateTime.UtcNow.AddMinutes(minutes).AddSeconds(seconds);
                        mysteryData.SetTimerEndTime(timerIndex, newEndTime);
                        mysteryData.SetTimerNotified(timerIndex, false);
                        SaveConfig();
                    }
                }
            }

            // Hint text
            string textStr = text;
            Vector2 hintSize = new Vector2(ImGui.GetContentRegionAvail().X, 60);
            if (ImGui.InputTextMultiline($"##{label}Text", ref textStr, 512, hintSize))
            {
                mysteryData.SetHintText(timerIndex, textStr);
                SaveConfig();
            }

            ImGui.Unindent();
            ImGui.Spacing();
        }

        private bool ParseTimeString(string timeStr, out int minutes, out int seconds)
        {
            minutes = 0;
            seconds = 0;

            if (string.IsNullOrWhiteSpace(timeStr))
                return false;

            var parts = timeStr.Split(':');
            if (parts.Length == 2)
            {
                return int.TryParse(parts[0], out minutes) && int.TryParse(parts[1], out seconds);
            }
            else if (parts.Length == 1)
            {
                return int.TryParse(parts[0], out minutes);
            }

            return false;
        }

        private void CheckVotingPeriod()
        {
            if (!votingStartTime.HasValue || config.CurrentGame == null)
                return;

            // Check if voting period ended
            if (DateTime.UtcNow - votingStartTime.Value >= votingDuration)
            {
                if (votingStartTime.HasValue) // Prevent multiple notifications
                {
                    chatGui.Print("[Murder Mystery] Voting period has ended!");
                    ProcessVotingResults();
                    votingStartTime = null;
                    SaveConfig();
                }
            }
        }

        private void CheckCountdownCompletion()
        {
            if (config.CurrentGame == null) return;

            var mysteryData = config.CurrentGame;
            var currentTime = DateTime.UtcNow;

            // Check all dynamic timers
            var endTimes = mysteryData.TimerEndTimes.ToList(); // Create a copy to avoid modification during iteration
            foreach (var kvp in endTimes)
            {
                int timerIndex = kvp.Key;
                DateTime endTime = kvp.Value;
                bool notified = mysteryData.GetTimerNotified(timerIndex);
                string text = mysteryData.GetHintText(timerIndex);

                if (!notified && endTime != DateTime.MinValue && currentTime >= endTime)
                {
                    chatGui.Print($"[Murder Mystery] Hint {timerIndex + 1} : {text}");
                    mysteryData.SetTimerNotified(timerIndex, true);
                    SaveConfig();
                }
            }
        }

        private void LoadOrCreatePlayerData(string playerName)
        {
            if (!config.PlayerDatabase.TryGetValue(playerName, out var data))
            {
                data = new PlayerData { Name = playerName };
                config.PlayerDatabase[playerName] = data;
                SaveConfig();
            }
            currentPlayerData = data;
        }

        private DateTime lastSaveTime = DateTime.MinValue;
        private readonly TimeSpan saveThrottle = TimeSpan.FromSeconds(2); // Only save every 2 seconds max

        private void SaveConfig()
        {
            try
            {
                // Check if enough time has passed since last save
                if ((DateTime.UtcNow - lastSaveTime) < saveThrottle)
                {
                    return;
                }

                lastSaveTime = DateTime.UtcNow;

                // Debug: Log what we're trying to save
                Plugin.Log.Debug($"Saving config: {config.MurderMysteryGames.Count} games");
                
                pluginInterface.SavePluginConfig(config);

                Plugin.Log.Debug("Config saved successfully");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Save failed: {ex}");
            }
        }
    }

    // Data model for a player
    [Serializable]
    public class PlayerData
    {
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";

        // Only use dynamic whisper storage
        public Dictionary<int, string> Whispers { get; set; } = new();

        // Legacy fields for migration - keep these temporarily
        [Obsolete("Use Whispers dictionary instead")]
        public string? Whisper1 { get; set; }
        [Obsolete("Use Whispers dictionary instead")]
        public string? Whisper2 { get; set; }
        [Obsolete("Use Whispers dictionary instead")]
        public string? Whisper3 { get; set; }
        [Obsolete("Use Whispers dictionary instead")]
        public Dictionary<int, string>? ExtraWhispers { get; set; }

        public string GetWhisper(int index)
        {
            return Whispers.TryGetValue(index, out string? value) ? value : "";
        }

        public void SetWhisper(int index, string value)
        {
            if (string.IsNullOrEmpty(value))
                Whispers.Remove(index);
            else
                Whispers[index] = value;
        }

        public int GetWhisperCount()
        {
            return Whispers.Keys.Count > 0 ? Whispers.Keys.Max() + 1 : 0;
        }
    }

    // Data model for Murder Mystery
    [Serializable]
    public class MurderMysteryData
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> ActivePlayers { get; set; } = new();
        public List<string> DeadPlayers { get; set; } = new();
        public List<string> ImprisonedPlayers { get; set; } = new();
        public string Killer { get; set; } = "";
        public string Prize { get; set; } = "";

        // Dynamic timers/hints - one for each round
        public Dictionary<int, string> HintTimes { get; set; } = new();
        public Dictionary<int, string> HintTexts { get; set; } = new();
        public Dictionary<int, DateTime> TimerEndTimes { get; set; } = new();
        public Dictionary<int, bool> TimerNotified { get; set; } = new();

        // Legacy fields for migration - keep these temporarily
        public bool? Timer3Notified { get; set; }

        public string GetHintTime(int index) => HintTimes.TryGetValue(index, out string? value) ? value : "";
        public void SetHintTime(int index, string value)
        {
            if (string.IsNullOrEmpty(value))
                HintTimes.Remove(index);
            else
                HintTimes[index] = value;
        }

        public string GetHintText(int index) => HintTexts.TryGetValue(index, out string? value) ? value : "";
        public void SetHintText(int index, string value)
        {
            if (string.IsNullOrEmpty(value))
                HintTexts.Remove(index);
            else
                HintTexts[index] = value;
        }

        public DateTime GetTimerEndTime(int index) => TimerEndTimes.TryGetValue(index, out DateTime value) ? value : DateTime.MinValue;
        public void SetTimerEndTime(int index, DateTime value)
        {
            if (value == DateTime.MinValue)
                TimerEndTimes.Remove(index);
            else
                TimerEndTimes[index] = value;
        }

        public bool GetTimerNotified(int index) => TimerNotified.TryGetValue(index, out bool value) && value;
        public void SetTimerNotified(int index, bool value)
        {
            if (!value)
                TimerNotified.Remove(index);
            else
                TimerNotified[index] = value;
        }

    }
}
