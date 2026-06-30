using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using xTile.Dimensions;

namespace helpfulSpouses
{
    internal sealed class ModEntry : Mod
    {
        NPC? npcObj;
        Queue<Vector2> positionHistory = new Queue<Vector2>();
        const int FOLLOW_DELAY = 20;
        static CompanionState _currentState = CompanionState.Idle;
        static IMonitor? _monitor;
        static bool _seedJustPlanted;
        static bool _isAnimatingWatering = false;
        string _currentOutfit = "default";

        Response[] choresOptions = new Response[]
        {
            new Response("follow", "Quiero que seas mi perra y me sigas"),
            new Response("shopping", "Ve con el maldito de pierre y compra algo")
        };

        public override void Entry(IModHelper helper)
        {
            _monitor = this.Monitor;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.DayStarted += this.GameLoop_DayStarted;
            helper.Events.GameLoop.OneSecondUpdateTicked += this.GameLoop_OneSecondUpdateTicked;
            helper.Events.Display.RenderedHud += this.OnRenderedHud;
            helper.Events.GameLoop.UpdateTicked += this.GameLoop_UpdateTicked;
            helper.Events.Player.Warped += this.Player_Warped;
            helper.Events.Player.InventoryChanged += this.Player_InventoryChanged;
            helper.Events.Content.AssetRequested += this.Content_AssetRequested;

            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), "returnHomeFromFarmPosition"),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(ReturnHomePatch))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), "checkAction",
                new[] { typeof(Farmer), typeof(GameLocation) }),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(CheckActionPatch))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), nameof(NPC.tryToReceiveActiveObject),
                new[] { typeof(Farmer), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(TryReceiveActiveObjectPatch))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), nameof(NPC.canTalk)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(CanTalkPatch))
            );
        }

        private string? ResolveOutfit(string npcName, string action, string outfit)
        {
            if (File.Exists(Path.Combine(Helper.DirectoryPath, $"assets/sprites/{npcName}/{action}_{outfit}.png")))
                return outfit;

            if (outfit != "default" && File.Exists(Path.Combine(Helper.DirectoryPath, $"assets/sprites/{npcName}/{action}_default.png")))
                return "default";

            return null;
        }

        private void Content_AssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            string prefix = "Characters/Companion_";
            string assetName = e.NameWithoutLocale.ToString();
            if (assetName.StartsWith(prefix))
            {
                string remaining = assetName.Substring(prefix.Length);
                int firstUnderscore = remaining.IndexOf('_');
                string npcName = remaining.Substring(0, firstUnderscore);
                string animationFile = remaining.Substring(firstUnderscore + 1);

                string modFilePath = $"assets/sprites/{npcName}/{animationFile}.png";
                if (File.Exists(Path.Combine(Helper.DirectoryPath, modFilePath)))
                    e.LoadFromModFile<Texture2D>(modFilePath, AssetLoadPriority.Medium);
            }
        }

        // Detects when the player consumes a seed from their inventory
        private void Player_InventoryChanged(object? sender, InventoryChangedEventArgs e)
        {
            if (!Context.IsWorldReady || _currentState == CompanionState.Idle) return;

            bool seedConsumed =
                e.QuantityChanged.Any(c => c.Item is StardewValley.Object obj &&
                                           obj.Category == StardewValley.Object.SeedsCategory &&
                                           c.NewSize < c.OldSize) ||
                e.Removed.Any(i => i is StardewValley.Object obj &&
                                   obj.Category == StardewValley.Object.SeedsCategory);

            if (seedConsumed)
                _seedJustPlanted = true;
        }

        private void GameLoop_OneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
        {
            if (npcObj == null) return;

            // React to a newly planted seed (give one tick of delay so crop data is ready)
            if (_seedJustPlanted && _currentState == CompanionState.Following)
            {
                _seedJustPlanted = false;
                TryEnterWateringMode();
            }

            switch (_currentState)
            {
                case CompanionState.Working:
                    break;
                case CompanionState.Watering:
                    WateringBehavior();
                    break;
            }
        }

        // Checks preconditions before switching into Watering state
        private void TryEnterWateringMode()
        {
            if (npcObj == null) return;

            var unwateredCrops = FindUnwateredCrops(Game1.player.currentLocation);
            if (unwateredCrops.Count == 0)
            {
                this.Monitor.Log("Seed planted but no unwatered crops found yet.", LogLevel.Debug);
                return;
            }

            _currentState = CompanionState.Watering;
            DeactivateControllers(npcObj); // stop any current movement cleanly
            Game1.addHUDMessage(new HUDMessage($"{npcObj.name} is going to water the crops!", 2));
            this.Monitor.Log($"Watering mode started. {unwateredCrops.Count} tile(s) to water.", LogLevel.Debug);
        }

        // Called every second while in Watering state
        private void WateringBehavior()
        {
            if (npcObj == null) return;
            npcObj.Sprite.SpriteWidth = 16;
            npcObj.Sprite.LoadTexture("Characters/" + npcObj.Name);
            _isAnimatingWatering = false;
            npcObj.farmerPassesThrough = true;
            // If the NPC is still walking to a tile, do nothing until it arrives
            if (npcObj.controller != null) return;

            var unwateredCrops = FindUnwateredCrops(npcObj.currentLocation);

            if (unwateredCrops.Count == 0)
            {
                // All done — return to Following and reset the breadcrumb trail
                _currentState = CompanionState.Following;
                ResetPositionHistory();
                Game1.addHUDMessage(new HUDMessage($"{npcObj.name} finished watering!", 2));
                return;
            }

            Vector2 targetCrop = unwateredCrops
                .OrderBy(tile => Vector2.Distance(Utility.PointToVector2(npcObj.TilePoint), tile))
                .First();

            Vector2 npcTile = Utility.PointToVector2(npcObj.TilePoint);
            Vector2[] adjacentOffsets = { new Vector2(0, 1), new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, -1) };

            Vector2 standingTile = adjacentOffsets
                .Select(offset => targetCrop + offset)
                .Where(tile => npcObj.currentLocation.isTilePassable(new Location((int)tile.X, (int)tile.Y), Game1.viewport))
                .OrderBy(tile => Vector2.Distance(npcTile, tile))
                .DefaultIfEmpty(targetCrop)
                .First();

            npcObj.controller = new PathFindController(npcObj, npcObj.currentLocation, new Point((int)standingTile.X, (int)standingTile.Y), 2)
            {
                endBehaviorFunction = (character, location) =>
                {
                    if (location.terrainFeatures.TryGetValue(targetCrop, out var feature) && feature is HoeDirt dirt)
                    {
                        string resolvedOutfit = ResolveOutfit(npcObj.Name, "watering", _currentOutfit);

                        if (resolvedOutfit != null)
                        {
                            npcObj.Sprite.SpriteWidth = 18;
                            npcObj.Sprite.LoadTexture($"Characters/Companion_{npcObj.Name}_watering_{resolvedOutfit}");
                            _isAnimatingWatering = true;
                        }
                        else
                        {
                            npcObj.doEmote(16);
                        }

                        Vector2 directionToTarget = targetCrop - Utility.PointToVector2(npcObj.TilePoint);
                        float absX = Math.Abs(directionToTarget.X);
                        float absY = Math.Abs(directionToTarget.Y);

                        if (absX > absY * 1.5f)
                            npcObj.FacingDirection = directionToTarget.X > 0 ? 1 : 3;
                        else if (absY > absX * 1.5f)
                            npcObj.FacingDirection = directionToTarget.Y > 0 ? 2 : 0;

                        Game1.playSound("wateringCan");
                        SpawnWateringParticles(location, targetCrop);
                        dirt.state.Value = 1;

                        this.Monitor.Log($"Watered tile at {targetCrop} from {standingTile}", LogLevel.Debug);
                    }
                }
            };
        }
        private void SpawnWateringParticles(GameLocation location, Vector2 targetCrop)
        {
            location.temporarySprites.Add(new TemporaryAnimatedSprite(
                13,
                new Vector2(targetCrop.X * 64f, targetCrop.Y * 64f),
                Color.White,
                10,
                Game1.random.NextBool(),
                70f,
                0,
                64,
                (targetCrop.Y * 64f + 32f) / 10000f - 0.01f
            ));

            if (npcObj.FacingDirection == 0) return;

            Vector2 sprayOrigin = new Vector2(npcObj.Position.X - 32f - 4f, npcObj.Position.Y - 16f - 4f);
            switch (npcObj.FacingDirection)
            {
                case 1: sprayOrigin.X += 136f; break;
                case 3: sprayOrigin.X -= 72f; break;
                case 2: sprayOrigin.X += 72f; sprayOrigin.Y += 44f; break;
            }

            Microsoft.Xna.Framework.Rectangle boundingBox = npcObj.GetBoundingBox();
            for (int i = 0; i < 30; i++)
            {
                location.temporarySprites.Add(new TemporaryAnimatedSprite(
                    "",
                    new Microsoft.Xna.Framework.Rectangle(0, 0, 1, 1),
                    999f,
                    1,
                    999,
                    sprayOrigin + new Vector2(Game1.random.Next(-3, 0) * 4, Game1.random.Next(2) * 4),
                    flicker: false,
                    flipped: false,
                    (float)(boundingBox.Bottom + 32) / 10000f,
                    0.04f,
                    Game1.random.Choose(Color.DeepSkyBlue, Color.LightBlue),
                    4f,
                    0f,
                    0f,
                    0f
                )
                {
                    delayBeforeAnimationStart = i * 15,
                    motion = new Vector2((float)Game1.random.Next(-10, 11) / 100f, 0.5f),
                    acceleration = new Vector2(0f, 0.1f)
                });
            }
        }

        // Returns only HoeDirt tiles that have a planted seed AND are dry
        private List<Vector2> FindUnwateredCrops(GameLocation location)
        {
            return location.terrainFeatures.Pairs
                .Where(pair => pair.Value is HoeDirt dirt &&
                               dirt.crop != null &&       // has a seed planted
                               dirt.state.Value != 1)     // is not yet watered
                .Select(pair => pair.Key)
                .ToList();
        }

        // Rebuilds the position history so the NPC doesn't snap when resuming Following
        private void ResetPositionHistory()
        {
            if (npcObj == null) return;
            positionHistory.Clear();
            for (int i = 0; i < FOLLOW_DELAY; i++)
                positionHistory.Enqueue(npcObj.Position);
        }

        static bool CanTalkPatch(NPC __instance, ref bool __result)
        {
            if (_currentState != CompanionState.Idle && __instance?.isMarried() == true)
            {
                __result = false;
                return false;
            }
            return true;
        }

        static bool TryReceiveActiveObjectPatch(NPC __instance)
        {
            if (_currentState != CompanionState.Idle && __instance?.isMarried() == true)
                return false;
            return true;
        }

        static bool CheckActionPatch(NPC __instance)
        {
            if (_currentState != CompanionState.Idle && __instance?.isMarried() == true)
                return false; // bloquea — aquí irá el mini menú
            return true;
        }

        static bool ReturnHomePatch(NPC __instance)
        {
            if (_currentState == CompanionState.Following && __instance?.isMarried() == true)
                return false;
            return true;
        }

        private void Player_Warped(object? sender, WarpedEventArgs e)
        {
            if(_currentState == CompanionState.Following && npcObj != null)
            {
                Game1.warpCharacter(npcObj, e.NewLocation.Name, Game1.player.TilePoint);
                npcObj.controller = null;
                npcObj.temporaryController = null;
                positionHistory.Clear();
                for (int i = 0; i < FOLLOW_DELAY; i++)
                    positionHistory.Enqueue(npcObj.Position);
            }
        }

        private void GameLoop_UpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (_currentState != CompanionState.Idle && npcObj != null)
            {
                Point cursorTile = new Point(
                    (Game1.getOldMouseX() + Game1.viewport.X) / 64,
                    (Game1.getOldMouseY() + Game1.viewport.Y) / 64
                );
                if (npcObj.TilePoint == cursorTile)
                    Game1.isSpeechAtCurrentCursorTile = false;
            }

            if (_currentState == CompanionState.Following && npcObj != null)
            {
                FollowingBehavior(npcObj, positionHistory);
            }
            if(_isAnimatingWatering == true && npcObj != null)
            {
                int startFrame = npcObj.FacingDirection switch
                {
                    0 => 8,
                    1 => 4,
                    2 => 0,
                    3 => 12,
                    _ => 0
                };
                npcObj.Sprite.Animate(Game1.currentGameTime, startFrame, 4, 230f);
            }
        }

        private void FollowingBehavior(NPC npc, Queue<Vector2> positions)
        {
            if (npc.currentLocation != Game1.player.currentLocation)
            {
                this.Monitor.Log($"Alex desaparecio y aparecio en: {npc.currentLocation.Name} cordenadas: {npc.TilePoint.ToString()}", LogLevel.Error);
                Game1.warpCharacter(npc, Game1.player.currentLocation,
                                    Utility.PointToVector2(Game1.player.TilePoint));
                DeactivateControllers(npc);
                positionHistory.Clear();
                for (int i = 0; i < FOLLOW_DELAY; i++)
                    positionHistory.Enqueue(npc.Position);
                this.Monitor.Log($"Se devolvio a alex a: {npc.currentLocation.Name} cordenadas: {npc.TilePoint.ToString()}", LogLevel.Warn);
                return;
            }

            positions.Enqueue(Game1.player.Position);

            if (positions.Count <= FOLLOW_DELAY) return;

            Vector2 targetPosition = positions.Dequeue();

            if (Vector2.Distance(npc.Position, Game1.player.Position) < 48f)
            {
                npc.Halt();
                return;
            }

            const float NPC_SPRITE_Y_OFFSET = 16f;

            Vector2 adjustedTarget = new Vector2(targetPosition.X, targetPosition.Y - NPC_SPRITE_Y_OFFSET);
            Vector2 direction = adjustedTarget - npc.Position;

            float absX = Math.Abs(direction.X);
            float absY = Math.Abs(direction.Y);

            if (absX > absY * 1.5f)
                npc.FacingDirection = direction.X > 0 ? 1 : 3; // Right : Left
            else if (absY > absX * 1.5f)
                npc.FacingDirection = direction.Y > 0 ? 2 : 0; // Down : Up

            int startFrame = npc.FacingDirection switch
            {
                0 => 8,
                1 => 4,
                2 => 0,
                3 => 12,
                _ => 0
            };

            if (direction.Length() > 1f)
            {
                npc.Sprite.Animate(Game1.currentGameTime, startFrame, 4, 100f);
                Vector2 move = direction;
                if (move.Length() > Game1.player.getMovementSpeed())
                    move = Vector2.Normalize(move) * Game1.player.getMovementSpeed();
                npc.Position += move;
            }
            else
            {
                npc.Halt();
            }
        }

        private void GameLoop_DayStarted(object? sender, DayStartedEventArgs e)
        {
            _currentState = CompanionState.Idle;

            try
            {
                NPC spouse = Game1.player.getSpouse();

                if (spouse != null)
                {
                    npcObj = spouse;
                }
                else
                {
                    this.Monitor.Log("Spouse is null.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error: {ex.Message}", LogLevel.Error);
            }
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            if (npcObj != null && GetDistance(Game1.player.TilePoint, npcObj.TilePoint) < 2)
            {
                if (e.Button == SButton.G)
                {
                    if (_currentState != CompanionState.Following)
                    {
                        ShowChoresMenu();
                    }
                    else
                    {
                        FreeNpc(npcObj);
                        Game1.addHUDMessage(new HUDMessage($"{npcObj.name} {Helper.Translation.Get("HUDmessage.stopFollowing")}", 2));
                    }
                }
            }
        }

        private void ActivateFollowing()
        {

            _currentState = CompanionState.Following;
            positionHistory.Clear();
            for (int i = 0; i < FOLLOW_DELAY; i++)
                positionHistory.Enqueue(npcObj.Position); // ← posición del npc, no del player
            npcObj.farmerPassesThrough = true;
            DeactivateControllers(npcObj);
            Game1.addHUDMessage(new HUDMessage($"{npcObj.name} {Helper.Translation.Get("HUDmessage.following")}", 2));
        }

        private void ShowChoresMenu()
        {
            npcObj.CurrentDialogue.Push(new Dialogue(npcObj, null, Helper.Translation.Get("chores.greeting")));
            Game1.drawDialogue(npcObj);

            // Wait for dialogue to close, then show options
            Helper.Events.Display.MenuChanged += OnDialogueClosed;
        }

        private void OnDialogueClosed(object? sender, MenuChangedEventArgs e)
        {
            if (e.OldMenu is not DialogueBox || e.NewMenu != null) return;

            Helper.Events.Display.MenuChanged -= OnDialogueClosed;

            Game1.currentLocation.createQuestionDialogue(
                Helper.Translation.Get("chores.question"),
                new Response[]
                {
              new Response("follow", Helper.Translation.Get("chores.follow")),
              new Response("cancel", Helper.Translation.Get("chores.cancel"))
                },
                HandleChoresChoice
            );
        }

        private void HandleChoresChoice(Farmer who, string selectedKey)
        {
            switch (selectedKey)
            {
                case "follow":
                    ActivateFollowing();
                    break;
                case "water":
                    // activar Watering
                    break;
            }
        }

        private void DeactivateControllers(NPC npc)
        {
            npc.temporaryController = null;
            npc.controller = null;
            npc.ignoreScheduleToday = true;
        }

        private int GetDistance(Point a, Point b)
        {
            return (int)Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        }

        private void FreeNpc(NPC npc)
        {
            walkToHome(npc);
            _currentState = CompanionState.Idle;
        }

        private void walkToHome(NPC npc)
        {
            FarmHouse casa = Utility.getHomeOfFarmer(Game1.player);

            Point puntoDeSalida = Game1.getLocationFromName("Farm").getWarpPointTo("FarmHouse", npc);

            if (puntoDeSalida != Point.Zero)
            {
                npc.controller = new PathFindController(npc, Game1.getLocationFromName("Farm"), puntoDeSalida, 2)
                {
                    NPCSchedule = true,
                    endBehaviorFunction = (character, location) => {
                        Game1.warpCharacter(npc, casa, Utility.PointToVector2(casa.getKitchenStandingSpot()));
                    }
                };
            }
        }

        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (npcObj == null) return;

            Vector2 localPos = npcObj.getLocalPosition(Game1.viewport);
            localPos.Y -= (npcObj.Sprite.SpriteHeight * 4) + 32;
            localPos.X += (npcObj.Sprite.SpriteWidth * 4) / 2f;

            Vector2 uiPos = Utility.ModifyCoordinatesForUIScale(localPos);
            Vector2 textSize = Game1.smallFont.MeasureString(_currentState.ToString());

            e.SpriteBatch.DrawString(
                Game1.smallFont,
                _currentState.ToString(),
                uiPos - (textSize / 2f),
                Color.White
            );
        }
    }
}