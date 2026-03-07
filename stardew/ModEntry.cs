using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using System;
using System.Linq;

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
            npcObj.farmerPassesThrough = true;
            if (npcObj == null) return;

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

            // Always go to the closest tile — greedy but good enough for now
            Vector2 target = unwateredCrops
                .OrderBy(tile => Vector2.Distance(Utility.PointToVector2(npcObj.TilePoint), tile))
                .First();

            npcObj.controller = new PathFindController(npcObj, npcObj.currentLocation, new Point((int)target.X, (int)target.Y), 2)
            {
                endBehaviorFunction = (character, location) =>
                {
                    if (location.terrainFeatures.TryGetValue(target, out var feature) && feature is HoeDirt dirt)
                    {
                        dirt.state.Value = 1; // 0 = dry, 1 = watered
                        npcObj!.doEmote(20);  // visual feedback (water drop)
                        this.Monitor.Log($"Watered tile at {target}", LogLevel.Debug);
                    }
                }
            };
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
            if (_currentState == CompanionState.Following && npcObj != null)
            {
                FollowingBehavior(npcObj, positionHistory);
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
                        _currentState = CompanionState.Following;
                        positionHistory.Clear();
                        for (int i = 0; i < FOLLOW_DELAY; i++)
                            positionHistory.Enqueue(npcObj.Position); // ← posición del NPC, no del player
                        npcObj.farmerPassesThrough = true;
                        DeactivateControllers(npcObj);
                        Game1.addHUDMessage(new HUDMessage($"{npcObj.name} is following you.", 2));
                    }
                    else
                    {
                        FreeNpc(npcObj);
                        Game1.addHUDMessage(new HUDMessage($"{npcObj.name} is not following you.", 2));
                    }
                }
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