﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using NLog;
using Surveillance.App.Json;
using Surveillance.App.RichPresence;
using Surveillance.Steam;
using Surveillance.Steam.Models;
using Surveillance.Steam.Response;

namespace Surveillance.App
{
    public class SurveillanceApp
    {
        public const uint DeadByDaylightAppId = 381210;
        private const int UpdateInterval = 1_000;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        
        public GameState[] GameStates { get; private set; }

        private readonly Dictionary<string, GameState> _gameStates = new Dictionary<string, GameState>();
        private readonly Dictionary<string, double> _stats = new Dictionary<string, double>();
        
        private readonly ResourceManager _resourceManager;
        private readonly IRichPresence[] _richPresences;
        private readonly int _updateRate;

        private bool _running;

        private bool _dirty;
        private GameState _gameState;

        public SurveillanceApp(IRichPresence[] richPresences)
        {
            _resourceManager = new ResourceManager("Surveillance.App.Resources.Strings", typeof(SurveillanceApp).Assembly);
            _richPresences = richPresences;
            _updateRate = richPresences.Select(rp => rp.UpdateRate).Max();
        }

        public void Run()
        {
            Logger.Info("Starting Surveillance");

            _running = true;

            LoadGameStates();
            RunRichPresenceLoop();
            RunStatsRequestLoop();

            while (_running)
            {
                foreach (var richPresence in _richPresences)
                    richPresence.PollEvents();
                Thread.Sleep(UpdateInterval);
            }

            Logger.Info("Shutting down");
        }
        
        private async void LoadGameStates()
        {
            _stats.Clear();
            
            var type = typeof(SurveillanceApp);
            var path = type.Namespace + ".Resources.GameStates.json";
            var resourceStream = type.Assembly.GetManifestResourceStream(path);
            GameStates = await JsonSerializer.DeserializeAsync<GameState[]>(resourceStream, DefaultJsonOptions.Instance);
            
            for (var i = 0; i < GameStates.Length; i++)
            {
                var gameState = GameStates[i];

                var character = gameState.Character;
                character.DisplayName = I18N("character." + character.Type + "." + character.Name);
                gameState.Character = character;

                var action = gameState.Action;
                action.DisplayName = I18N("action." + action.Type + "." + action.Name);
                gameState.Action = action;

                GameStates[i] = gameState;
            }
            
            foreach (var gameState in GameStates)
            foreach (var trigger in gameState.Triggers)
            {
                Logger.Debug("Registering trigger \"{0}\" for \"{1}\"", trigger, gameState);
                _gameStates[trigger] = gameState;
            }
        }
        
        private async void RunRichPresenceLoop()
        {
            foreach (var richPresence in _richPresences)
                await richPresence.Init(this);

            while (_running)
            {
                if (!_dirty)
                    continue;

                Logger.Debug("Dispatching update to rich presence (state: {0})", _gameState);
                foreach (var richPresence in _richPresences)
                    richPresence.UpdateGameState(_gameState);

                _dirty = false;
                await Task.Delay(_updateRate);
            }

            foreach (var richPresence in _richPresences)
                richPresence.Dispose();
        }

        private async void RunStatsRequestLoop()
        {
            var steamKey = Environment.GetEnvironmentVariable("STEAM_KEY");
            SteamApi.Init(steamKey);

            var requestUri = BuildUri();

            while (_running)
            {
                var apiResponse = await SteamApi.Request<SteamUserStatsForGameResponse>(requestUri, DefaultJsonOptions.Instance);
                var apiResponseContent = apiResponse.Content;
                var playerStats = apiResponseContent.PlayerStats;

                Logger.Info("Received stats of player {0} for {1}", playerStats.SteamId, playerStats.GameName);

                UpdateGameState(playerStats.Stats);

                var utcNow = DateTimeOffset.UtcNow;
                var offset = apiResponse.Expires.Subtract(utcNow);
                Logger.Trace("Next steam request in {0} (now: {1}, expires: {2})", offset, utcNow, apiResponse.Expires);
                await Task.Delay(offset);
            }

            SteamApi.Reset();
        }

        private void UpdateGameState(IEnumerable<SteamGameStatModel> stats)
        {
            GameState? newState = null;
            foreach (var stat in stats)
            {
                var statName = stat.Name;
                if (!_gameStates.TryGetValue(statName, out var gameState))
                    continue;

                if (_stats.TryGetValue(statName, out var oldStat))
                {
                    Logger.Trace("Comparing trigger \"{0}\" (current: {1}, new: {2}) for {3}", statName, oldStat, stat.Value, gameState);
                    if (Math.Abs(oldStat - stat.Value) <= 0)
                        continue;

                    Logger.Trace("Updating trigger value: {0}", stat.Value);
                    newState = gameState;
                    _stats[statName] = stat.Value;
                }
                else
                {
                    Logger.Trace("Registering initial value: {0}", stat.Value);
                    _stats[statName] = stat.Value;
                }
            }
            
            if(newState.HasValue)
                SetGameState(newState.Value);
        }

        public void SetGameState(GameState gameState)
        {
            Logger.Info("Updating game state to {0}", gameState);
            
            _gameState = gameState;

            var gameCharacter = gameState.Character;
            var role = I18N("character.role." + gameCharacter.Type);
            _gameState.State = I18N("character.role.playing_as", role);
            
            var gameAction = gameState.Action;
            var triggers = gameState.Triggers;
            var triggerCount = triggers.Length;
            var stats = new object[triggerCount];
            for (var i = 0; i < triggerCount; i++)
                stats[i] = _stats.TryGetValue(triggers[i], out var s) ? Math.Floor(s) : 0;
            _gameState.Details = I18N("action." + gameAction.Type + "." + gameAction.Name + ".details", stats);
            
            _dirty = true;
        }

        private static Uri BuildUri()
        {
            var builder = new UriBuilder("https://api.steampowered.com/ISteamUserStats/GetUserStatsForGame/v2/");

            var queryCollection = HttpUtility.ParseQueryString(builder.Query);
            queryCollection["key"] = Environment.GetEnvironmentVariable("STEAM_KEY") ?? throw new ArgumentException("Missing STEAM_KEY");
            queryCollection["appid"] = DeadByDaylightAppId.ToString(NumberFormatInfo.InvariantInfo);
            queryCollection["steamid"] = Environment.GetEnvironmentVariable("STEAM_ID") ?? throw new ArgumentException("Missing STEAM_ID");
            builder.Query = queryCollection.ToString() ?? throw new InvalidOperationException();

            return builder.Uri;
        }

        public string I18N(string key, params object[] args)
        {
            return string.Format(_resourceManager.GetString(key) ?? key, args);
        }

        public void Close()
        {
            _running = false;
        }
    }
}