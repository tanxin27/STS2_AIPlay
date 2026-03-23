using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using STS2_AIPlay.Handlers;
using STS2_AIPlay.Llm;

namespace STS2_AIPlay;

public partial class AutoSlayNode : Node
{
    private double _cooldown;
    private double _combatCardDelay;
    private double _logTimer;
    private string? _lastLog;
    private bool _ftueDisabled;
    private IDisposable? _cardSelectorScope;
    private AutoSlayCardSelector? _cardSelector;
    private readonly System.Random _rng = new();

    // LLM state
    private LlmClient? _llm;
    private ArchetypeManager? _archetypeManager;  // 流派管理器
    private ExperienceUpdater? _experienceUpdater; // 经验更新器
    private List<CardPickInfo> _cardPicksThisRun = new(); // 本局卡牌选择记录
    private LearningSystem? _learningSystem; // 结构化学习系统
    private CombatTacticsLearner? _combatTactics; // 战斗战术学习器 (Phase 1 & 2)
    private AdvancedTacticsLearner? _advancedTactics; // 高级战术学习器 (Phase 3)
    private Task<string>? _pendingLlm;
    private string? _pendingContext; // "combat", "map", "overlay:TypeName", "event", "restsite"
    private List<CombatAction>? _combatPlan;
    private int _combatPlanStep;
    private bool _combatTurnRequested; // prevent re-requesting same turn
    private bool _combatPlanEndTurn; // true if LLM explicitly said END_TURN after plays
    private List<int>? _shopPlan; // list of item indices to buy
    private int _shopPlanStep;
    private bool _shopInventoryOpened;
    private bool _shopLeaving;
    private bool _gameOverReflected;
    private int _rewardCardChoice; // card choice from rewards screen, -1 = skip
    private Queue<NRewardButton>? _rewardTakeQueue; // queued reward button refs to click
    private bool _rewardsLlmDone; // true after LLM plan has been executed for current rewards screen
    private bool _restSiteChoiceMade; // true after LLM rest site choice is executed

    public override void _Ready()
    {
        bool active = CommandLineHelper.HasArg("autoslay");
        SetProcess(active);
        if (!active) return;

        var config = LlmConfig.Load();
        if (config != null)
        {
            _llm = new LlmClient(config);
            // 初始化流派系统
            _archetypeManager = new ArchetypeManager();
            _experienceUpdater = new ExperienceUpdater(_archetypeManager);
            _cardPicksThisRun.Clear();
            MainFile.Logger.Info("[AutoSlay] 流派识别系统已初始化");
            
            // 初始化结构化学习系统
            _learningSystem = new LearningSystem();
            _learningSystem.StartNewRun(Guid.NewGuid().ToString(), "UNKNOWN");
            MainFile.Logger.Info("[AutoSlay] 结构化学习系统已初始化");
            
            // 初始化战斗战术学习器
            _combatTactics = new CombatTacticsLearner();
            MainFile.Logger.Info("[AutoSlay] 战斗战术学习器已初始化");
            
            // 初始化高级战术学习器 (Phase 3)
            _advancedTactics = new AdvancedTacticsLearner();
            MainFile.Logger.Info("[AutoSlay] 高级战术学习器已初始化");
        }

        // Register card selector to auto-handle mid-combat card selections (e.g. Armaments)
        // These use ICardSelector, not overlay screens, so they need this even with LLM
        _cardSelector = new AutoSlayCardSelector(_rng, _llm);
        _cardSelectorScope = CardSelectCmd.UseSelector(_cardSelector);

        MainFile.Logger.Info(_llm != null
            ? "[AutoSlay] Bot active with LLM decision-making."
            : "[AutoSlay] Bot active with random decision-making (no llm_config.json).");
    }

    public override void _ExitTree()
    {
        _cardSelectorScope?.Dispose();
        _cardSelectorScope = null;
    }

    public override void _Process(double delta)
    {
        // Wait for card selector's async LLM request to finish before doing anything
        if (_cardSelector?.IsPendingLlm == true)
            return;

        _cooldown -= delta;
        _combatCardDelay -= delta;
        _logTimer -= delta;

        // Disable all FTUEs and unlock everything once SaveManager is available
        if (!_ftueDisabled && SaveManager.Instance != null)
        {
            SaveManager.Instance.SetFtuesEnabled(enabled: false);
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
            UnlockAll();
            _ftueDisabled = true;
            MainFile.Logger.Info("[AutoSlay] FTUEs disabled, fast mode enabled, all content unlocked.");
        }

        // Keep seed override set (NGame.Instance may not exist at _Ready time)
        if (NGame.Instance != null && NGame.Instance.DebugSeedOverride == null)
        {
            NGame.Instance.DebugSeedOverride = "AUTOSLAY42";
            MainFile.Logger.Info("[AutoSlay] Seed override set to AUTOSLAY42");
        }

        // ── Check pending LLM call ───────────────────────────────────────────
        if (_pendingLlm != null)
        {
            if (!_pendingLlm.IsCompleted) return; // still waiting

            try
            {
                if (_pendingLlm.IsFaulted)
                {
                    MainFile.Logger.Info($"[AutoSlay/LLM] Request failed: {_pendingLlm.Exception?.InnerException?.Message}");
                    _pendingLlm = null;
                    _pendingContext = null;
                    _cooldown = 1.0;
                    return;
                }

                var response = _pendingLlm.Result;
                _pendingLlm = null;
                ExecuteLlmResult(_pendingContext!, response);
                _pendingContext = null;
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[AutoSlay/LLM] Error handling response: {ex.Message}");
                _pendingLlm = null;
                _pendingContext = null;
                _cooldown = 1.0;
            }
            return;
        }

        // ── Handle overlays that appear mid-combat (e.g. Headbutt card selection) ──
        if (NOverlayStack.Instance?.ScreenCount > 0 && CombatManager.Instance?.IsInProgress == true)
        {
            var overlay = NOverlayStack.Instance.Peek();
            var overlayNode = overlay as Node;
            if (overlayNode != null)
            {
                if (_llm != null && TryRequestLlmForOverlay(overlayNode))
                    return;
                if (_cooldown <= 0)
                {
                    _cooldown = DispatchOverlay(overlayNode);
                    return;
                }
                return; // wait for cooldown before handling overlay
            }
        }

        // ── Execute combat plan steps ────────────────────────────────────────
        if (_combatPlan != null)
        {
            var cm2 = CombatManager.Instance;
            if (cm2 == null || !cm2.IsInProgress || !cm2.IsPlayPhase)
            {
                _combatPlan = null;
                _combatTurnRequested = false;
                return;
            }
            if (_combatCardDelay > 0) return;

            ExecuteNextCombatStep();
            return;
        }

        // ── Combat (highest priority) ────────────────────────────────────────
        var cm = CombatManager.Instance;
        if (cm != null && cm.IsInProgress)
        {
            // CombatHandler.BoostHpIfNeeded();

            if (cm.IsPlayPhase)
            {
                if (_combatCardDelay > 0) return;

                if (_llm != null && !_combatTurnRequested)
                {
                    // Request LLM for full turn plan
                    // 生成战术提示
                    var tacticsHint = GenerateCombatTacticsHint(cm);
                    var prompt = GameStateSerializer.SerializeCombat(cm, tacticsHint);
                    _pendingLlm = _llm.SendAsync(prompt, "combat");
                    _pendingContext = "combat";
                    _combatTurnRequested = true;
                    return;
                }

                if (_llm == null)
                {
                    CombatHandler.UsePotionsIfNeeded(_rng);
                    _combatCardDelay = CombatHandler.PlayOneCard(cm, _rng);
                }
                // If LLM mode and waiting, do nothing (handled above)
            }
            else
            {
                CombatHandler.OnNonPlayPhase();
                _combatTurnRequested = false;
            }
            return;
        }
        CombatHandler.OnCombatEnded();
        _combatTurnRequested = false;

        if (_cooldown > 0) return;

        // ── Log state each tick ──────────────────────────────────────────────
        LogState();

        // ── Overlay screens ──────────────────────────────────────────────────
        if (NOverlayStack.Instance?.ScreenCount > 0)
        {
            var overlay = NOverlayStack.Instance.Peek();
            var overlayNode = overlay as Node;
            // Skip rewards overlay when LLM is done with rewards — fall through to map
            if (overlayNode is NRewardsScreen rewardsForProceed && _rewardsLlmDone)
            {
                var proceed = AutoSlayHelpers.FindFirst<NProceedButton>(rewardsForProceed);
                if (proceed?.IsEnabled == true)
                {
                    proceed.ForceClick();
                    _cooldown = 1.0;
                    return;
                }
                // fall through to map/room handling below
            }
            else if (overlayNode != null)
            {
                if (_llm != null && TryRequestLlmForOverlay(overlayNode))
                    return;
                _cooldown = DispatchOverlay(overlayNode);
                return;
            }
            else
            {
                return;
            }
        }
        _rewardsLlmDone = false; // reset once we're past overlays

        RewardsHandler.ClearTried();

        // ── Map ──────────────────────────────────────────────────────────────
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null && mapScreen.IsOpen)
        {
            _gameOverReflected = false;
            RunSummaryLogger.Reset();
            if (_llm != null)
            {
                var prompt = GameStateSerializer.SerializeMap(mapScreen);
                _pendingLlm = _llm.SendAsync(prompt, "map");
                _pendingContext = "map";
                return;
            }
            LogOnce("Handling map");
            _cooldown = MapHandler.Handle(mapScreen, _rng);
            return;
        }

        // ── Event room ───────────────────────────────────────────────────────
        var eventRoom = GetNodeOrNull<Node>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
        if (eventRoom != null)
        {
            if (_llm != null)
            {
                var options = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom)
                    .Where(o => !o.Option.IsLocked).ToList();
                if (options.Count == 1 && options[0].Option.IsProceed)
                {
                    // Only option is Proceed — auto-click without LLM
                    options[0].ForceClick();
                    _cooldown = 1.0;
                    return;
                }
                if (options.Count > 0)
                {
                    var prompt = GameStateSerializer.SerializeEvent(eventRoom);
                    _pendingLlm = _llm.SendAsync(prompt, "event");
                    _pendingContext = "event";
                    return;
                }
            }
            LogOnce("Handling event room");
            _cooldown = EventRoomHandler.Handle(eventRoom, _rng);
            return;
        }

        // ── Treasure room ────────────────────────────────────────────────────
        var treasureRoom = GetNodeOrNull<NTreasureRoom>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/TreasureRoom");
        if (treasureRoom != null)
        {
            LogOnce("Handling treasure room");
            _cooldown = TreasureRoomHandler.Handle(treasureRoom);
            return;
        }

        // ── Rest site ────────────────────────────────────────────────────────
        var restRoom = GetNodeOrNull<NRestSiteRoom>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/RestSiteRoom");
        if (restRoom != null)
        {
            if (_llm != null && !_restSiteChoiceMade)
            {
                var btns = AutoSlayHelpers.FindAll<NRestSiteButton>(restRoom)
                    .Where(b => b.Option.IsEnabled).ToList();
                if (btns.Count > 0)
                {
                    var prompt = GameStateSerializer.SerializeRestSite(restRoom);
                    _pendingLlm = _llm.SendAsync(prompt, "restsite");
                    _pendingContext = "restsite";
                    return;
                }
            }
            if (_restSiteChoiceMade)
            {
                // Choice already made — just wait for proceed button
                var proceed = restRoom.ProceedButton;
                if (proceed?.IsEnabled == true)
                {
                    MainFile.Logger.Info("[AutoSlay] Clicking rest site proceed");
                    proceed.ForceClick();
                    _cooldown = 1.5;
                }
                else
                {
                    _cooldown = 0.5;
                }
            }
            else
            {
                LogOnce("Handling rest site");
                _cooldown = RestSiteHandler.Handle(restRoom, _rng);
            }
            return;
        }
        _restSiteChoiceMade = false; // reset when we leave the rest site

        // ── Shop ─────────────────────────────────────────────────────────────
        var shopRoom = GetNodeOrNull<NMerchantRoom>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/MerchantRoom");
        if (shopRoom != null)
        {
            // Leaving state: click proceed when available
            if (_shopLeaving)
            {
                if (shopRoom.ProceedButton?.IsEnabled == true)
                {
                    shopRoom.ProceedButton.ForceClick();
                    _shopLeaving = false;
                    _shopInventoryOpened = false;
                    _cooldown = 1.0;
                }
                else
                {
                    // Close inventory if still open
                    AutoSlayHelpers.FindFirst<NBackButton>(shopRoom)?.ForceClick();
                    _cooldown = 0.5;
                }
                return;
            }
            if (_llm != null && _pendingLlm == null && _shopPlan == null)
            {
                // Open inventory if not already open
                if (!_shopInventoryOpened)
                {
                    shopRoom.OpenInventory();
                    _shopInventoryOpened = true;
                    _cooldown = 0.5;
                    return;
                }
                LogOnce("Handling shop (LLM)");
                var prompt = GameStateSerializer.SerializeShop(shopRoom);
                _pendingLlm = _llm.SendAsync(prompt, "shop");
                _pendingContext = "shop";
                return;
            }
            if (_shopPlan != null)
            {
                ExecuteNextShopStep(shopRoom);
                return;
            }
            if (_llm == null)
            {
                LogOnce("Handling shop (random)");
                TaskHelper.RunSafely(ShopHandler.HandleAsync(shopRoom, _rng));
                _cooldown = 60.0;
                return;
            }
            return;
        }
        else
        {
            _shopInventoryOpened = false;
            _shopLeaving = false;
        }

        // ── Victory proceed ──────────────────────────────────────────────────
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            var proceed = combatRoom.ProceedButton;
            if (proceed != null && proceed.IsEnabled)
            {
                LogOnce("Clicking combat room proceed");
                proceed.ForceClick();
                _cooldown = 1.0;
                return;
            }
        }

        // ── Main Menu — auto-start run ─────────────────────────────────────
        var mainMenu = GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
        if (mainMenu != null && mainMenu.IsVisibleInTree())
        {
            _cooldown = HandleMainMenu(mainMenu);
            return;
        }

        // ── Nothing matched ──────────────────────────────────────────────────
        if (_logTimer <= 0)
        {
            var overlayCount = NOverlayStack.Instance?.ScreenCount ?? -1;
            var mapOpen = NMapScreen.Instance?.IsOpen ?? false;
            var cmInProgress = cm?.IsInProgress ?? false;
            MainFile.Logger.Info($"[AutoSlay] Idle: overlays={overlayCount} map={mapOpen} combat={cmInProgress}");
            _logTimer = 5.0;
        }
    }

    private double HandleMainMenu(Control mainMenu)
    {
        // Check if character select is already open — select Ironclad and embark
        var charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
        if (charSelect != null && charSelect.Visible)
        {
            // Try to click embark if already selected
            var embark = charSelect.GetNodeOrNull<NConfirmButton>("ConfirmButton");
            if (embark != null && embark.IsEnabled)
            {
                // Select character: use const for fixed, or uncomment random line for random selection
                const string CHARACTER_ID = "SILENT"; // Fixed character - change this to IRONCLAD/SILENT/DEFECT/WATCHER
                // string[] CHARACTERS = { "IRONCLAD", "SILENT", "DEFECT", "WATCHER" };
                // string CHARACTER_ID = CHARACTERS[_rng.Next(CHARACTERS.Length)]; // Random character
                
                // Notify LLM of character selection for per-character memory
                _llm?.SetCharacter(CHARACTER_ID);
                
                var buttonContainer = charSelect.GetNodeOrNull<Node>("CharSelectButtons/ButtonContainer");
                if (buttonContainer != null)
                {
                    // Try preferred character first
                    foreach (var btn in AutoSlayHelpers.FindAll<NCharacterSelectButton>(buttonContainer))
                    {
                        if (!btn.IsLocked && btn.Character?.Id.Entry == CHARACTER_ID)
                        {
                            btn.Select();
                            MainFile.Logger.Info($"[AutoSlay] Selected character: {CHARACTER_ID}");
                            break;
                        }
                    }
                }
                LogOnce("Clicking embark");
                embark.ForceClick();
                return 3.0;
            }
            return 0.5; // waiting for embark to become enabled
        }

        // Check if singleplayer submenu is open — click Standard
        var standardBtn = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
        if (standardBtn != null && standardBtn.Visible && standardBtn.IsEnabled)
        {
            LogOnce("Clicking Standard run");
            standardBtn.ForceClick();
            return 1.0;
        }

        // Abandon existing run if needed
        var abandonBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/AbandonRunButton");
        if (abandonBtn != null && abandonBtn.Visible && abandonBtn.IsEnabled)
        {
            // Check if confirmation popup is open
            var modal = NModalContainer.Instance?.OpenModal;
            if (modal != null)
            {
                var yesBtn = ((Node)modal).GetNodeOrNull<NButton>("VerticalPopup/YesButton");
                if (yesBtn != null && yesBtn.IsEnabled)
                {
                    LogOnce("Confirming abandon run");
                    yesBtn.ForceClick();
                    return 1.5;
                }
                return 0.5;
            }
            LogOnce("Abandoning existing run");
            abandonBtn.ForceClick();
            return 1.0;
        }

        // Click singleplayer button
        var spBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/SingleplayerButton");
        if (spBtn != null && spBtn.Visible && spBtn.IsEnabled)
        {
            LogOnce("Clicking Singleplayer");
            spBtn.ForceClick();
            return 1.0;
        }

        return 1.0; // waiting for menu to be ready
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LLM overlay request — returns true if LLM call was started
    // ─────────────────────────────────────────────────────────────────────────

    private bool TryRequestLlmForOverlay(Node overlayNode)
    {
        MainFile.Logger.Info($"[AutoSlay/DBG] TryRequestLlm overlay={overlayNode.GetType().Name} takeQ={(_rewardTakeQueue == null ? "null" : $"[{string.Join(", ", _rewardTakeQueue.Select(b => b.Reward?.GetType().Name ?? "?"))}]")} llmDone={_rewardsLlmDone} cardChoice={_rewardCardChoice}");

        if (overlayNode is NCardRewardSelectionScreen)
        {
            if (_rewardCardChoice != 0)
            {
                ApplyCardRewardChoice(_rewardCardChoice);
                _rewardCardChoice = 0;
                return true;
            }
            // Always ask LLM for card reward selections (supports multiple card rewards)
            // 获取当前卡组和角色信息用于流派分析
            var (character, deck, relics, act, floor) = GetCurrentRunState();
            var prompt = GameStateSerializer.SerializeCardReward(
                (NCardRewardSelectionScreen)overlayNode,
                _archetypeManager,
                character,
                deck,
                relics,
                act);
            _pendingLlm = _llm!.SendAsync(prompt, "overlay:CardReward");
            _pendingContext = "overlay:CardReward";
            return true;
        }
        if (overlayNode is NRewardsScreen rewardsScreen)
        {
            // Process queued takes first
            if (_rewardTakeQueue != null && _rewardTakeQueue.Count > 0)
            {
                var btn = _rewardTakeQueue.Dequeue();
                if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
                {
                    MainFile.Logger.Info($"[AutoSlay/LLM] Taking reward: {btn.Reward?.GetType().Name}");
                    btn.ForceClick();
                }
                _cooldown = 0.8;
                return true;
            }
            // Queue exhausted — proceed
            if (_rewardTakeQueue != null)
            {
                _rewardTakeQueue = null;
                _rewardsLlmDone = true;
                // Don't proceed yet — card reward overlay may still need processing
                _cooldown = 0.5;
                return true;
            }
            // No queue — check if there are actual rewards to choose from
            var availableBtns = AutoSlayHelpers.FindAll<NRewardButton>(rewardsScreen)
                .Where(b => b.IsEnabled).ToList();
            if (availableBtns.Count == 0)
            {
                // No rewards left — just proceed
                var proceed = AutoSlayHelpers.FindFirst<NProceedButton>(rewardsScreen);
                if (proceed?.IsEnabled == true)
                    proceed.ForceClick();
                else
                    NOverlayStack.Instance?.Remove(rewardsScreen);
                _cooldown = 1.0;
                return true;
            }
            // Ask LLM
            var prompt = GameStateSerializer.SerializeRewards(rewardsScreen);
            _pendingLlm = _llm!.SendAsync(prompt, "overlay:Rewards");
            _pendingContext = "overlay:Rewards";
            return true;
        }
        if (overlayNode is NDeckUpgradeSelectScreen or NDeckTransformSelectScreen
            or NDeckEnchantSelectScreen or NDeckCardSelectScreen)
        {
            // If LLM already chose, let DispatchOverlay -> CardGridHandler.Handle() apply it
            if (CardGridHandler.HasPendingLlmChoice)
                return false;

            // Only ask LLM in Phase 1 (no preview, no confirm enabled yet)
            var mainConfirm = overlayNode.GetNodeOrNull<NConfirmButton>("Confirm")
                ?? overlayNode.GetNodeOrNull<NConfirmButton>("%Confirm");
            if (mainConfirm?.IsEnabled != true)
            {
                var screenType = overlayNode switch
                {
                    NDeckUpgradeSelectScreen => "UPGRADE A CARD",
                    NDeckTransformSelectScreen => "TRANSFORM A CARD",
                    NDeckEnchantSelectScreen => "ENCHANT A CARD",
                    NDeckCardSelectScreen => "REMOVE A CARD",
                    _ => "CHOOSE A CARD"
                };
                var prompt = GameStateSerializer.SerializeCardGrid(overlayNode, screenType);
                _pendingLlm = _llm!.SendAsync(prompt, "overlay:CardGrid");
                _pendingContext = "overlay:CardGrid";
                return true;
            }
        }
        if (overlayNode is NSimpleCardSelectScreen simpleScreen)
        {
            // If LLM already chose, let DispatchOverlay -> SimpleCardSelectHandler.Handle() apply it
            if (SimpleCardSelectHandler.HasPendingLlmChoice)
                return false;

            // If confirm is already enabled, let DispatchOverlay handle it (card already selected or skip)
            var simpleConfirm = simpleScreen.GetNodeOrNull<NConfirmButton>("%Confirm");
            if (simpleConfirm?.IsEnabled == true)
                return false;

            // Ask LLM to choose a card
            var cards = AutoSlayHelpers.FindAll<NGridCardHolder>(overlayNode);
            if (cards.Count > 0)
            {
                var prompt = GameStateSerializer.SerializeCardGrid(overlayNode, "CHOOSE A CARD");
                _pendingLlm = _llm!.SendAsync(prompt, "overlay:SimpleCardSelect");
                _pendingContext = "overlay:SimpleCardSelect";
                return true;
            }
        }
        // Other overlays: use random handler (no strategic value)
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Execute LLM response based on context
    // ─────────────────────────────────────────────────────────────────────────

    private void ExecuteLlmResult(string context, string response)
    {
        switch (context)
        {
            case "combat":
                ParseAndStartCombatPlan(response);
                break;

            case "map":
                ExecuteMapChoice(response);
                break;

            case "event":
                ExecuteEventChoice(response);
                break;

            case "restsite":
                ExecuteRestSiteChoice(response);
                break;

            case "overlay:CardReward":
                ExecuteCardRewardChoice(response);
                break;

            case "overlay:Rewards":
                ExecuteRewardsChoice(response);
                break;

            case "overlay:CardGrid":
                ExecuteCardGridChoice(response);
                break;

            case "overlay:SimpleCardSelect":
                ExecuteSimpleCardSelectChoice(response);
                break;

            case "shop":
                ParseAndStartShopPlan(response);
                break;

            case "gameover_reflection":
                MainFile.Logger.Info($"[AutoSlay/LLM] Reflection: {response.Replace("\n", " | ")}");
                _llm?.SaveMemory(response);
                _llm?.ResetForNewRun();
                GameStateSerializer.ResetMapTracking();
                _gameOverReflected = true;
                _cooldown = 1.0;
                break;

            default:
                MainFile.Logger.Info($"[AutoSlay/LLM] Unknown context: {context}");
                _cooldown = 1.0;
                break;
        }
    }

    private void ParseAndStartCombatPlan(string response)
    {
        var plan = new List<CombatAction>();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) { _cooldown = 0.5; return; }

        var hand = PileType.Hand.GetPile(player).Cards.ToList();
        var potions = player.Potions.Where(p => !p.IsQueued).ToList();
        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        var enemies = combatState?.Enemies.Where(e => e.IsAlive).ToList() ?? new List<Creature>();
        bool hasEndTurn = false;

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("END_TURN", StringComparison.OrdinalIgnoreCase))
            {
                hasEndTurn = true;
                break;
            }

            if (trimmed.StartsWith("PLAY ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Substring(5).Split("->", StringSplitOptions.TrimEntries);
                if (!int.TryParse(parts[0].Trim(), out int cardIdx)) continue;
                if (cardIdx < 1 || cardIdx > hand.Count) continue;

                var target = ParseEnemyTarget(parts, enemies);
                plan.Add(new CombatAction(hand[cardIdx - 1], null, target));
            }
            else if (trimmed.StartsWith("POTION ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Substring(7).Split("->", StringSplitOptions.TrimEntries);
                var potionStr = parts[0].Trim().TrimStart('P', 'p');
                if (!int.TryParse(potionStr, out int potionIdx)) continue;
                if (potionIdx < 1 || potionIdx > potions.Count) continue;

                var target = ParseEnemyTarget(parts, enemies);
                plan.Add(new CombatAction(null, potions[potionIdx - 1], target));
            }
        }

        if (plan.Count == 0 && hasEndTurn)
        {
            MainFile.Logger.Info("[AutoSlay/LLM] LLM chose to end turn immediately");
            if (CombatManager.Instance?.IsPlayPhase == true)
                PlayerCmd.EndTurn(player, canBackOut: false);
            _combatCardDelay = 0.5;
            return;
        }

        if (plan.Count == 0)
        {
            MainFile.Logger.Info("[AutoSlay/LLM] No valid actions parsed from combat response, ending turn");
            if (CombatManager.Instance?.IsPlayPhase == true)
                PlayerCmd.EndTurn(player, canBackOut: false);
            _combatCardDelay = 0.5;
            return;
        }

        MainFile.Logger.Info($"[AutoSlay/LLM] Combat plan: {plan.Count} actions, endTurn={hasEndTurn}");
        _combatPlan = plan;
        _combatPlanEndTurn = hasEndTurn;
        _combatPlanStep = 0;
    }

    private static Creature? ParseEnemyTarget(string[] parts, List<Creature> enemies)
    {
        if (parts.Length <= 1) return null;
        var targetStr = parts[1].Trim().ToUpperInvariant();
        if (targetStr.Length == 1 && targetStr[0] >= 'A')
        {
            int enemyIdx = targetStr[0] - 'A';
            if (enemyIdx >= 0 && enemyIdx < enemies.Count)
                return enemies[enemyIdx];
        }
        return null;
    }

    private void ExecuteNextCombatStep()
    {
        if (_combatPlan == null) return;

        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) { _combatPlan = null; return; }

        if (_combatPlanStep >= _combatPlan.Count)
        {
            _combatPlan = null;
            if (_combatPlanEndTurn)
            {
                // LLM explicitly said END_TURN — end the turn
                MainFile.Logger.Info("[AutoSlay/LLM] Combat plan complete, ending turn");
                if (CombatManager.Instance?.IsPlayPhase == true)
                    PlayerCmd.EndTurn(player, canBackOut: false);
                _combatCardDelay = 0.5;
            }
            else
            {
                // No END_TURN — re-query LLM to see updated hand (draw effects, etc.)
                // Wait longer if overlays are open (e.g. potion triggered card selection)
                _combatTurnRequested = false;
                var overlayOpen = NOverlayStack.Instance?.ScreenCount > 0;
                _combatCardDelay = overlayOpen ? 2.0 : 0.5;
                MainFile.Logger.Info($"[AutoSlay/LLM] Combat plan complete, re-evaluating hand (overlay={overlayOpen})");
            }
            return;
        }

        var action = _combatPlan[_combatPlanStep];
        _combatPlanStep++;

        if (action.Potion != null)
        {
            // Use potion
            if (action.Potion.HasBeenRemovedFromState || action.Potion.IsQueued)
            {
                MainFile.Logger.Info($"[AutoSlay/LLM] Skipping potion {action.Potion.Id.Entry} (already used)");
                return;
            }
            // Use LLM-specified target, or fall back to official game logic
            var potionTarget = action.Target
                ?? PotionHelper.GetTarget(action.Potion, player?.Creature?.CombatState);
            MainFile.Logger.Info($"[AutoSlay/LLM] Using potion {action.Potion.Id.Entry}{(action.Target != null ? $" -> {action.Target.Monster?.Id.Entry}" : "")}");
            action.Potion.EnqueueManualUse(potionTarget);
            _combatCardDelay = 1.5; // potions may trigger card selection overlays
            return;
        }

        if (action.Card != null)
        {
            // Play card
            var hand = PileType.Hand.GetPile(player).Cards;
            if (!hand.Contains(action.Card) || !action.Card.CanPlay(out _, out _))
            {
                MainFile.Logger.Info($"[AutoSlay/LLM] Skipping {action.Card.Id.Entry} (no longer playable)");
                return;
            }
            MainFile.Logger.Info($"[AutoSlay/LLM] Playing {action.Card.Id.Entry}{(action.Target != null ? $" -> {action.Target.Monster?.Id.Entry}" : "")}");
            action.Card.TryManualPlay(action.Target);
            _combatCardDelay = 0.4;
        }
    }

    private void ExecuteMapChoice(string response)
    {
        int choice = ParseChoice(response);
        var mapScreen = NMapScreen.Instance;
        if (mapScreen?.IsOpen != true) { _cooldown = 1.0; return; }

        var points = AutoSlayHelpers.FindAll<NMapPoint>(mapScreen)
            .Where(p => p.IsEnabled).ToList();

        if (choice >= 1 && choice <= points.Count)
        {
            var point = points[choice - 1];
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var currentCoord = runState?.CurrentMapCoord;
            var player = runState != null ? LocalContext.GetMe(runState) : null;
            
            // Record route choice for learning
            if (currentCoord.HasValue && player != null)
            {
                RouteHistoryLogger.RecordChoice(
                    fromRow: currentCoord.Value.row,
                    fromCol: currentCoord.Value.col,
                    toRow: point.Point.coord.row,
                    toCol: point.Point.coord.col,
                    roomType: point.Point.PointType.ToString(),
                    currentHp: player.Creature.CurrentHp,
                    maxHp: player.Creature.MaxHp,
                    gold: player.Gold,
                    reasoning: response
                );
            }
            
            MainFile.Logger.Info($"[AutoSlay/LLM] Map choice: {choice} -> ({point.Point.coord.row},{point.Point.coord.col})");
            mapScreen.OnMapPointSelectedLocally(point);
        }
        else if (points.Count > 0)
        {
            // Fallback to random
            var point = points[_rng.Next(points.Count)];
            mapScreen.OnMapPointSelectedLocally(point);
        }
        _cooldown = 2.0;
    }

    private void ExecuteEventChoice(string response)
    {
        int choice = ParseChoice(response);
        var eventRoom = GetNodeOrNull<Node>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
        if (eventRoom == null) { _cooldown = 1.0; return; }

        var options = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom)
            .Where(o => !o.Option.IsLocked).ToList();

        if (choice >= 1 && choice <= options.Count)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Event choice: {choice}");
            options[choice - 1].ForceClick();
        }
        else if (options.Count > 0)
        {
            options[_rng.Next(options.Count)].ForceClick();
        }
        _cooldown = 1.0;
    }

    private void ExecuteRestSiteChoice(string response)
    {
        int choice = ParseChoice(response);
        var restRoom = GetNodeOrNull<NRestSiteRoom>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/RestSiteRoom");
        if (restRoom == null) { _cooldown = 1.0; return; }

        var btns = AutoSlayHelpers.FindAll<NRestSiteButton>(restRoom)
            .Where(b => b.Option.IsEnabled).ToList();

        if (choice >= 1 && choice <= btns.Count)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Rest site choice: {choice}");
            btns[choice - 1].ForceClick();
        }
        else if (btns.Count > 0)
        {
            btns[_rng.Next(btns.Count)].ForceClick();
        }
        _restSiteChoiceMade = true;
        _cooldown = 1.5;
    }

    private void ExecuteCardRewardChoice(string response)
    {
        int choice = ParseChoice(response);
        MainFile.Logger.Info("666 ApplyCardRewardChoice from ExecuteCardRewardChoice");
        ApplyCardRewardChoice(choice);
    }

    private void ApplyCardRewardChoice(int choice)
    {
        var cardReward = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen;
        if (cardReward == null) { _cooldown = 1.0; return; }

        if (choice <= 0)
        {
            // Skip — click the alternative/skip button
            var altBtn = AutoSlayHelpers.FindFirst<NCardRewardAlternativeButton>(cardReward);
            if (altBtn != null)
            {
                MainFile.Logger.Info("[AutoSlay/LLM] Clicking skip button on card reward");
                altBtn.ForceClick();
            }
            else
            {
                MainFile.Logger.Info("[AutoSlay/LLM] No skip button found, removing overlay");
                NOverlayStack.Instance?.Remove(cardReward);
            }
            _cooldown = 1.0;
            return;
        }

        var holders = AutoSlayHelpers.FindAll<NCardHolder>(cardReward);
        if (choice >= 1 && choice <= holders.Count)
        {
            var selectedCard = holders[choice - 1].CardModel;
            var cardName = selectedCard?.Id.Entry ?? "Unknown";
            MainFile.Logger.Info($"[AutoSlay/LLM] Card reward choice: {choice} ({cardName})");
            
            // 记录选择用于经验学习
            var otherOptions = holders.Select(h => h.CardModel?.Id.Entry ?? "Unknown")
                .Where(c => c != cardName)
                .ToList();
            RecordCardPick(cardName, otherOptions);
            
            holders[choice - 1].EmitSignal(NCardHolder.SignalName.Pressed, holders[choice - 1]);
        }
        else
        {
            MainFile.Logger.Info("[AutoSlay/LLM] Invalid card choice, clicking skip");
            var altBtn = AutoSlayHelpers.FindFirst<NCardRewardAlternativeButton>(cardReward);
            if (altBtn != null) altBtn.ForceClick();
            else NOverlayStack.Instance?.Remove(cardReward);
        }
        _cooldown = 1.5;
    }

    private void ExecuteRewardsChoice(string response)
    {
        // Parse TAKE, CARD, and DONE commands
        var takeIndices = new List<int>();
        int cardChoice = -1;

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim().ToUpper();
            if (trimmed.StartsWith("TAKE") && trimmed.Length > 4)
            {
                if (int.TryParse(trimmed.Substring(4).Trim(), out int idx))
                    takeIndices.Add(idx);
            }
            else if (trimmed.StartsWith("CARD") && trimmed.Length > 4)
            {
                if (int.TryParse(trimmed.Substring(4).Trim(), out int idx))
                    cardChoice = idx;
            }
        }

        MainFile.Logger.Info($"[AutoSlay/LLM] Reward plan: {takeIndices.Count} takes, card={cardChoice}");

        // Store card choice for when card reward screen opens
        _rewardCardChoice = cardChoice; // -1 = skip, >0 = pick that card

        // Resolve indices to actual button references NOW (before any buttons get disabled)
        var rewardsScreen = NOverlayStack.Instance?.Peek() as NRewardsScreen;
        var btns = rewardsScreen != null
            ? AutoSlayHelpers.FindAll<NRewardButton>(rewardsScreen).Where(b => b.IsEnabled).ToList()
            : new List<NRewardButton>();

        var queue = new Queue<NRewardButton>();
        foreach (var idx in takeIndices)
        {
            if (idx >= 1 && idx <= btns.Count)
            {
                var btn = btns[idx - 1];
                // Skip card reward buttons — handled separately below
                if (btn.Reward is MegaCrit.Sts2.Core.Rewards.CardReward)
                    continue;
                queue.Enqueue(btn);
            }
            else
                MainFile.Logger.Info($"[AutoSlay/LLM] Invalid reward index {idx} (max={btns.Count})");
        }

        // If LLM wants a card, enqueue the card reward button at the end
        if (cardChoice > 0)
        {
            var cardBtn = btns.FirstOrDefault(b => b.Reward is MegaCrit.Sts2.Core.Rewards.CardReward);
            if (cardBtn != null)
                queue.Enqueue(cardBtn);
        }

        _rewardTakeQueue = queue;
        _cooldown = 0.3;
    }

    private void ExecuteCardGridChoice(string response)
    {
        int choice = ParseChoice(response);
        MainFile.Logger.Info($"[AutoSlay/LLM] Card grid choice: {choice}");
        CardGridHandler.SetLlmChoice(choice);
        _cooldown = 0.5; // delay to let screen fully initialize before handler interacts
    }

    private void ExecuteSimpleCardSelectChoice(string response)
    {
        int choice = ParseChoice(response);
        MainFile.Logger.Info($"[AutoSlay/LLM] Simple card select choice: {choice}");
        SimpleCardSelectHandler.SetLlmChoice(choice);
        _cooldown = 0.5; // delay to let screen fully initialize before handler interacts
    }

    private void ParseAndStartShopPlan(string response)
    {
        var buys = new List<int>();
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim().ToUpper();
            if (trimmed.StartsWith("BUY") && trimmed.Length > 3)
            {
                var numStr = trimmed.Substring(3).Trim();
                if (int.TryParse(numStr, out int idx))
                    buys.Add(idx);
            }
            if (trimmed == "LEAVE") break;
        }
        MainFile.Logger.Info($"[AutoSlay/LLM] Shop plan: {buys.Count} purchases");
        if (buys.Count == 0)
        {
            // Nothing to buy, leave immediately
            _shopPlan = null;
            LeaveShop();
            return;
        }
        _shopPlan = buys;
        _shopPlanStep = 0;
        _cooldown = 0.3;
    }

    private void ExecuteNextShopStep(NMerchantRoom shopRoom)
    {
        if (_shopPlan == null) return;

        if (_shopPlanStep >= _shopPlan.Count)
        {
            MainFile.Logger.Info("[AutoSlay/LLM] Shop plan complete, leaving");
            _shopPlan = null;
            LeaveShop();
            return;
        }

        var inv = shopRoom.Inventory?.Inventory;
        if (inv == null) { _shopPlan = null; _cooldown = 1.0; return; }

        // Build the same indexed list as serializer
        var items = new List<MerchantEntry>();
        foreach (var e in inv.CardEntries) if (e.IsStocked) items.Add(e);
        foreach (var e in inv.RelicEntries) if (e.IsStocked) items.Add(e);
        foreach (var e in inv.PotionEntries) if (e.IsStocked) items.Add(e);
        if (inv.CardRemovalEntry?.IsStocked == true) items.Add(inv.CardRemovalEntry);

        int idx = _shopPlan[_shopPlanStep];
        _shopPlanStep++;

        if (idx >= 1 && idx <= items.Count)
        {
            var entry = items[idx - 1];
            if (entry.IsStocked && entry.EnoughGold)
            {
                MainFile.Logger.Info($"[AutoSlay/LLM] Buying item {idx}: {entry.GetType().Name} ({entry.Cost}g)");
                TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inv));
                _cooldown = 1.5;
                return;
            }
            MainFile.Logger.Info($"[AutoSlay/LLM] Skipping item {idx}: not stocked or not enough gold");
        }
        _cooldown = 0.3;
    }

    private void LeaveShop()
    {
        MainFile.Logger.Info("[AutoSlay/LLM] Leaving shop");
        _shopLeaving = true;
        _cooldown = 1.5;
    }

    private static int ParseChoice(string response)
    {
        // Look for "CHOOSE <number>" pattern first
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("CHOOSE ", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed.Substring(7).Trim(), out int val))
                    return val;
            }
        }

        // Fallback: find first number in response
        foreach (var ch in response)
        {
            if (char.IsDigit(ch))
                return ch - '0';
        }
        return -1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Random overlay dispatch (used when LLM is off or for non-strategic overlays)
    // ─────────────────────────────────────────────────────────────────────────

    private double DispatchOverlay(Node overlayNode)
    {
        if (overlayNode is NCardRewardSelectionScreen cardReward)
        {
            LogOnce("Handling card reward overlay");
            return CardRewardHandler.Handle(cardReward, _rng);
        }
        if (overlayNode is NRewardsScreen rewardsScreen)
        {
            LogOnce("Handling rewards screen");
            return RewardsHandler.Handle(rewardsScreen);
        }
        if (overlayNode is NGameOverScreen gameOver)
        {
            RunSummaryLogger.TryLog(_llm);
            
            // Save route history for analysis
            var asmDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            RouteHistoryLogger.SaveToFile(asmDir);
            
            // Update archetype experience learning
            UpdateArchetypeExperienceOnGameOver();
            
            // Update structured learning system
            UpdateStructuredLearningOnGameOver();
            
            if (_llm != null && !_gameOverReflected)
            {
                if (_pendingLlm == null)
                {
                    LogOnce("Requesting game over reflection from LLM");
                    var stats = RunSummaryLogger.LastRunStats ?? "No stats available";
                    var routeAnalysis = RouteHistoryLogger.GetRouteAnalysisForReflection();
                    if (!string.IsNullOrEmpty(routeAnalysis))
                    {
                        stats += "\n\n" + routeAnalysis;
                    }
                    
                    // Add structured learning data
                    var structuredData = GetStructuredLearningDataForReflection();
                    if (!string.IsNullOrEmpty(structuredData))
                    {
                        stats += "\n\n" + structuredData;
                    }
                    
                    var currentMemory = _llm.Memory.Length > 0 ? _llm.Memory : "(empty — this is your first run)";
                    var characterName = _llm?.Character ?? "UNKNOWN";
                    var prompt = PromptStrings.Get("GameOverReflection", stats, currentMemory, characterName);
                    _pendingLlm = _llm.SendAsync(prompt, "gameover_reflection");
                    _pendingContext = "gameover_reflection";
                    return 1.0;
                }
                return 0.5; // waiting for reflection response
            }
            LogOnce("Handling game over screen");
            return GameOverHandler.Handle(gameOver);
        }
        if (overlayNode is NChooseACardSelectionScreen chooseCard)
        {
            LogOnce("Handling choose-a-card screen");
            return ChooseACardHandler.Handle(chooseCard, _rng);
        }
        if (overlayNode is NChooseABundleSelectionScreen chooseBundle)
        {
            LogOnce("Handling choose-a-bundle screen");
            return ChooseABundleHandler.Handle(chooseBundle, _rng);
        }
        if (overlayNode is NChooseARelicSelection chooseRelic)
        {
            LogOnce("Handling choose-a-relic screen");
            return ChooseARelicHandler.Handle(chooseRelic, _rng);
        }
        if (overlayNode is NDeckUpgradeSelectScreen or NDeckTransformSelectScreen
            or NDeckEnchantSelectScreen or NDeckCardSelectScreen)
        {
            LogOnce($"Handling {overlayNode.GetType().Name}");
            return CardGridHandler.Handle(overlayNode, _rng);
        }
        if (overlayNode is NSimpleCardSelectScreen simpleSelect)
        {
            LogOnce("Handling simple card select screen");
            return SimpleCardSelectHandler.Handle(simpleSelect, _rng);
        }
        if (overlayNode is NCrystalSphereScreen crystalSphere)
        {
            LogOnce("Handling crystal sphere screen");
            return CrystalSphereHandler.Handle(crystalSphere, _rng);
        }

        // Unknown overlay — try proceed/back
        LogOnce($"Unknown overlay: {overlayNode.GetType().Name}");
        var proceed = AutoSlayHelpers.FindFirst<NProceedButton>(overlayNode)
            ?? AutoSlayHelpers.FindFirst<NBackButton>(overlayNode) as NClickableControl;
        if (proceed != null)
        {
            MainFile.Logger.Info($"[AutoSlay] Clicking proceed/back on {overlayNode.GetType().Name}");
            proceed.ForceClick();
            return 1.0;
        }
        return 0.5;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Logging
    // ─────────────────────────────────────────────────────────────────────────

    private void LogState()
    {
        var stack = NOverlayStack.Instance;
        var overlayCount = stack?.ScreenCount ?? 0;
        var parts = new List<string>();
        parts.Add($"overlays={overlayCount}");
        if (overlayCount > 0 && stack != null)
        {
            var field = typeof(NOverlayStack).GetField("_overlays",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(stack) is System.Collections.IList overlays)
            {
                var names = new List<string>();
                foreach (var s in overlays)
                    names.Add(s?.GetType().Name ?? "null");
                parts.Add($"stack=[{string.Join(", ", names)}]");
            }
            else
            {
                var top = stack.Peek();
                parts.Add($"top={top?.GetType().Name ?? "null"}");
            }
        }
        var mapOpen = NMapScreen.Instance?.IsOpen ?? false;
        if (mapOpen) parts.Add("map=open");
        var cmActive = CombatManager.Instance?.IsInProgress ?? false;
        if (cmActive) parts.Add("combat=active");
        if (_llm != null) parts.Add($"llm_msgs={_llm.MessageCount}");
        MainFile.Logger.Info($"[AutoSlay] Tick: {string.Join(" ", parts)}");
    }

    private void LogOnce(string msg)
    {
        if (msg == _lastLog) return;
        _lastLog = msg;
        MainFile.Logger.Info($"[AutoSlay] {msg}");
    }

    private static void UnlockAll()
    {
        var progress = SaveManager.Instance.Progress;

        // Unlock all epochs (characters, content tiers)
        foreach (var epochId in EpochModel.AllEpochIds)
        {
            SaveManager.Instance.ObtainEpochOverride(epochId, EpochState.Revealed);
        }

        // Unlock all cards, relics, potions, events
        foreach (var card in ModelDb.AllCards)
            progress.MarkCardAsSeen(card.Id);
        foreach (var relic in ModelDb.AllRelics)
            progress.MarkRelicAsSeen(relic.Id);
        foreach (var potion in ModelDb.AllPotions)
            progress.MarkPotionAsSeen(potion.Id);
        foreach (var evt in ModelDb.AllEvents)
            progress.MarkEventAsSeen(evt.Id);

        SaveManager.Instance.SaveProgressFile();
    }

    private record CombatAction(CardModel? Card, PotionModel? Potion, Creature? Target);

    // ─────────────────────────────────────────────────────────────────────────
    // Archetype / Build System Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取当前游戏状态，用于流派分析
    /// </summary>
    private (string character, List<string> deck, List<string> relics, int act, int floor) GetCurrentRunState()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return ("UNKNOWN", new List<string>(), new List<string>(), 1, 1);
            
            var player = LocalContext.GetMe(runState);
            if (player == null) return ("UNKNOWN", new List<string>(), new List<string>(), 1, 1);
            
            var character = player.Character?.Id.Entry ?? "UNKNOWN";
            var deck = player.Deck?.Cards?.Select(c => c.Id.Entry).ToList() ?? new List<string>();
            var relics = player.Relics?.Select(r => r.Id.Entry).ToList() ?? new List<string>();
            var floor = runState.TotalFloor;
            var act = floor <= 17 ? 1 : floor <= 34 ? 2 : 3;
            
            return (character, deck, relics, act, floor);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] 获取游戏状态失败: {ex.Message}");
            return ("UNKNOWN", new List<string>(), new List<string>(), 1, 1);
        }
    }

    /// <summary>
    /// 记录卡牌选择（用于后续经验学习）
    /// </summary>
    private void RecordCardPick(string cardId, List<string> otherOptions)
    {
        if (_experienceUpdater == null) return;
        
        var (_, _, _, _, floor) = GetCurrentRunState();
        _cardPicksThisRun.Add(new CardPickInfo
        {
            CardId = cardId,
            Floor = floor,
            Context = $"Act {(floor <= 17 ? 1 : floor <= 34 ? 2 : 3)}, Floor {floor}",
            OtherOptions = otherOptions
        });
    }

    /// <summary>
    /// 游戏结束时更新流派经验
    /// </summary>
    private void UpdateArchetypeExperienceOnGameOver()
    {
        if (_experienceUpdater == null) return;
        
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return;
            
            var player = LocalContext.GetMe(runState);
            if (player == null) return;
            
            var character = player.Character?.Id.Entry ?? "UNKNOWN";
            var victory = runState.CurrentRoom?.IsVictoryRoom ?? false;
            var floor = runState.TotalFloor;
            var deck = player.Deck?.Cards?.Select(c => c.Id.Entry).ToList() ?? new List<string>();
            var relics = player.Relics?.Select(r => r.Id.Entry).ToList() ?? new List<string>();
            
            // 更新决策结果
            _experienceUpdater.UpdateDecisionResults(character, victory, floor);
            
            // 更新整体经验
            _experienceUpdater.UpdateFromRun(character, victory, floor, deck, relics, _cardPicksThisRun);
            
            // 清空本局记录
            _cardPicksThisRun.Clear();
            
            MainFile.Logger.Info($"[AutoSlay] 流派经验已更新: {character}, 胜利: {victory}, 层数: {floor}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] 更新流派经验失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 更新结构化学习系统
    /// </summary>
    private void UpdateStructuredLearningOnGameOver()
    {
        if (_learningSystem == null) return;
        
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return;
            
            var player = LocalContext.GetMe(runState);
            if (player == null) return;
            
            var character = player.Character?.Id.Entry ?? "UNKNOWN";
            var victory = runState.CurrentRoom?.IsVictoryRoom ?? false;
            var floor = runState.TotalFloor;
            var killedBy = GetKilledBy(runState);
            var deck = player.Deck?.Cards?.Select(c => c.Id.Entry).ToList() ?? new List<string>();
            var relics = player.Relics?.Select(r => r.Id.Entry).ToList() ?? new List<string>();
            var hp = player.Creature.CurrentHp;
            var maxHp = player.Creature.MaxHp;
            var gold = player.Gold;
            
            // 检测主流派
            var (primaryArchetype, _) = DetectArchetype(deck);
            
            // 完成学习
            _learningSystem.FinalizeRun(
                Guid.NewGuid().ToString(),
                character,
                victory,
                floor,
                killedBy,
                deck,
                relics,
                hp,
                maxHp,
                gold,
                primaryArchetype);
            
            MainFile.Logger.Info($"[AutoSlay] 结构化学习已更新: {character}, 胜利: {victory}, 层数: {floor}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] 更新结构化学习失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 获取结构化学习数据用于LLM反思
    /// </summary>
    private string GetStructuredLearningDataForReflection()
    {
        if (_learningSystem == null) return "";
        
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return "";
            
            var player = LocalContext.GetMe(runState);
            if (player == null) return "";
            
            var character = player.Character?.Id.Entry ?? "UNKNOWN";
            var victory = runState.CurrentRoom?.IsVictoryRoom ?? false;
            var floor = runState.TotalFloor;
            var killedBy = GetKilledBy(runState);
            
            return _learningSystem.GenerateStructuredReflectionData(character, victory, floor, killedBy);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] 获取结构化学习数据失败: {ex.Message}");
            return "";
        }
    }
    
    /// <summary>
    /// 获取被谁击杀
    /// </summary>
    private string? GetKilledBy(MegaCrit.Sts2.Core.Runs.RunState runState)
    {
        var history = RunManager.Instance?.History;
        if (history == null) return null;
        
        if (history.KilledByEncounter != MegaCrit.Sts2.Core.Models.ModelId.none)
            return history.KilledByEncounter.Entry;
        if (history.KilledByEvent != MegaCrit.Sts2.Core.Models.ModelId.none)
            return history.KilledByEvent.Entry;
        
        return null;
    }
    
    /// <summary>
    /// 简单检测主流派
    /// </summary>
    private (string? archetype, float confidence) DetectArchetype(List<string> deck)
    {
        // 毒流检测
        var poisonCards = new[] { "Noxious Fumes", "Catalyst", "Poisoned Stab", "Bane", "Deadly Poison", "Envenom" };
        var poisonCount = deck.Count(c => poisonCards.Any(p => c.Contains(p)));
        if (poisonCount >= 3) return ("Poison", poisonCount / 4.0f);
        
        // 刀流检测
        var shivCards = new[] { "Blade Dance", "Accuracy", "Infinite Blades", "Storm of Steel" };
        var shivCount = deck.Count(c => shivCards.Any(s => c.Contains(s)));
        if (shivCount >= 2) return ("Shiv", shivCount / 3.0f);
        
        // 弃牌流检测
        var discardCards = new[] { "Tactician", "Reflex", "Eviscerate", "Concentrate" };
        var discardCount = deck.Count(c => discardCards.Any(d => c.Contains(d)));
        if (discardCount >= 2) return ("Discard", discardCount / 3.0f);
        
        return (null, 0);
    }
    
    // ─────────────────────────────────────────────────────────────────────────
    // Combat Tactics Learning
    // ─────────────────────────────────────────────────────────────────────────
    
    /// <summary>
    /// 生成战斗战术提示 (Phase 1 + 2 + 3)
    /// </summary>
    private string GenerateCombatTacticsHint(CombatManager cm)
    {
        var sb = new System.Text.StringBuilder();
        
        try
        {
            var combatState = cm.DebugOnlyGetState();
            if (combatState?.Enemies == null || !combatState.Enemies.Any()) return "";
            
            var enemy = combatState.Enemies.FirstOrDefault(e => e.IsAlive);
            if (enemy == null) return "";
            
            var enemyId = enemy.Monster?.Id.Entry ?? "Unknown";
            var enemyName = enemyId;
            var intent = GetEnemyIntentDescription(enemy);
            var turnNumber = EstimateCombatTurn();
            
            // Phase 1 & 2: 基础和回合分析
            if (_combatTactics != null)
            {
                sb.AppendLine(_combatTactics.GenerateTacticsHint(enemyId, enemyName, intent, turnNumber));
            }
            
            // Phase 3: 高级战术提示
            if (_advancedTactics != null)
            {
                var runState = RunManager.Instance?.DebugOnlyGetState();
                var player = runState != null ? LocalContext.GetMe(runState) : null;
                
                if (player != null)
                {
                    var hand = PileType.Hand.GetPile(player).Cards.Select(c => c.Id.Entry).ToList();
                    var currentEnergy = player.PlayerCombatState?.Energy ?? 0;
                    var playerHp = player.Creature.CurrentHp;
                    var playerMaxHp = player.Creature.MaxHp;
                    
                    var advancedHint = _advancedTactics.GenerateAdvancedHint(
                        enemyId, hand, turnNumber, playerHp, playerMaxHp,
                        currentEnergy, enemy.CurrentHp, enemy.MaxHp);
                    
                    sb.AppendLine(FormatAdvancedHint(advancedHint));
                }
            }
            
            return sb.ToString();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] 生成战术提示失败: {ex.Message}");
            return sb.ToString();
        }
    }
    
    /// <summary>
    /// 格式化Phase 3高级提示
    /// </summary>
    private string FormatAdvancedHint(AdvancedTacticsHint hint)
    {
        var sb = new System.Text.StringBuilder();
        
        if (!string.IsNullOrEmpty(hint.UrgentWarning))
        {
            sb.AppendLine();
            sb.AppendLine($"🚨 {hint.UrgentWarning}");
        }
        
        if (!string.IsNullOrEmpty(hint.ComboSuggestion))
        {
            sb.AppendLine();
            sb.AppendLine(hint.ComboSuggestion);
        }
        
        if (!string.IsNullOrEmpty(hint.PotionAdvice))
        {
            sb.AppendLine();
            sb.AppendLine(hint.PotionAdvice);
        }
        
        if (!string.IsNullOrEmpty(hint.EnergyTip))
        {
            sb.AppendLine();
            sb.AppendLine(hint.EnergyTip);
        }
        
        if (hint.DangerAlerts.Any())
        {
            sb.AppendLine();
            sb.AppendLine("⚠️ 危险预警:");
            foreach (var alert in hint.DangerAlerts.Skip(1).Take(2))
            {
                sb.AppendLine($"  • {alert}");
            }
        }
        
        if (hint.OptimizationTips.Any())
        {
            sb.AppendLine();
            sb.AppendLine("💡 优化建议:");
            foreach (var tip in hint.OptimizationTips.Take(2))
            {
                sb.AppendLine($"  • {tip}");
            }
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 获取敌人意图描述
    /// </summary>
    private string GetEnemyIntentDescription(Creature enemy)
    {
        var move = enemy.Monster?.NextMove;
        if (move == null) return "Unknown";
        
        var intents = new List<string>();
        foreach (var intent in move.Intents)
        {
            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent attack)
            {
                try
                {
                    var dmg = attack.GetTotalDamage(enemy.CombatState?.PlayerCreatures ?? new List<Creature>(), enemy);
                    intents.Add($"Attack {dmg}");
                }
                catch
                {
                    intents.Add("Attack");
                }
            }
            else
            {
                intents.Add(intent.IntentType.ToString());
            }
        }
        
        return string.Join(" + ", intents);
    }
    
    /// <summary>
    /// 估算当前战斗回合数
    /// </summary>
    private int EstimateCombatTurn()
    {
        // 简化估算：可以从CombatManager获取更准确的信息
        return 1;
    }
}
