﻿#region using directives

using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Common;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Logging;
using PoGo.NecroBot.Logic.State;
using PoGo.NecroBot.Logic.Utils;
using PokemonGo.RocketAPI.Extensions;
using POGOProtos.Map.Fort;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class FarmPokestopsTask
    {
        public static async Task Execute(Context ctx, StateMachine machine)
        {
            var distanceFromStart = LocationUtils.CalculateDistanceInMeters(
                ctx.Settings.DefaultLatitude, ctx.Settings.DefaultLongitude,
                ctx.Client.CurrentLatitude, ctx.Client.CurrentLongitude);

            // Edge case for when the client somehow ends up outside the defined radius
            if (ctx.LogicSettings.MaxTravelDistanceInMeters != 0 &&
                distanceFromStart > ctx.LogicSettings.MaxTravelDistanceInMeters)
            {
                Logger.Write(ctx.Translations.GetTranslation(TranslationString.FarmPokestopsOutsideRadius, distanceFromStart), LogLevel.Warning);

                await Task.Delay(5000);

                await ctx.Navigation.HumanLikeWalking(
                    new GeoCoordinate(ctx.Settings.DefaultLatitude, ctx.Settings.DefaultLongitude),
                    ctx.LogicSettings.WalkingSpeedInKilometerPerHour, null);
            }

            var pokestopList = await GetPokeStops(ctx);
            var stopsHit = 0;

            if (pokestopList.Count <= 0)
            {
                machine.Fire(new WarnEvent { Message = ctx.Translations.GetTranslation(TranslationString.FarmPokestopsNoUsableFound) });
            }

            machine.Fire(new PokeStopListEvent { Forts = pokestopList });

            while (pokestopList.Any())
            {
                //resort
                pokestopList =
                    pokestopList.OrderBy(
                        i =>
                            LocationUtils.CalculateDistanceInMeters(ctx.Client.CurrentLatitude,
                                ctx.Client.CurrentLongitude, i.Latitude, i.Longitude)).ToList();
                var pokeStop = pokestopList[0];
                pokestopList.RemoveAt(0);

                var distance = LocationUtils.CalculateDistanceInMeters(ctx.Client.CurrentLatitude,
                    ctx.Client.CurrentLongitude, pokeStop.Latitude, pokeStop.Longitude);
                var fortInfo = await ctx.Client.Fort.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);

                // Dirty hack for pokeball quantity, needs to be pulled out to a isStopNeeded() method to expand other preferences like other minimal items quantity
                if (!ctx.LogicSettings.MinimalStops || (ctx.LogicSettings.MinimalStops && (
                    25 > ctx.Inventory.GetItemAmountByType(POGOProtos.Inventory.Item.ItemId.ItemPokeBall).Result + ctx.Inventory.GetItemAmountByType(POGOProtos.Inventory.Item.ItemId.ItemGreatBall).Result + ctx.Inventory.GetItemAmountByType(POGOProtos.Inventory.Item.ItemId.ItemUltraBall).Result
                )))
                {
                    machine.Fire(new FortTargetEvent { Name = fortInfo.Name, Distance = distance });
                }

                await ctx.Navigation.HumanLikeWalking(new GeoCoordinate(pokeStop.Latitude, pokeStop.Longitude),
                    ctx.LogicSettings.WalkingSpeedInKilometerPerHour,
                    async () =>
                    {
                        // Catch normal map Pokemon
                        await CatchNearbyPokemonsTask.Execute(ctx, machine);
                        //Catch Incense Pokemon
                        await CatchIncensePokemonsTask.Execute(ctx, machine);
                        return true;
                    });

                //Catch Lure Pokemon
                if (pokeStop.LureInfo != null)
                {
                    await CatchLurePokemonsTask.Execute(ctx, machine, pokeStop);
                }

                // Dirty hack for pokeball quantity, needs to be pulled out to a isStopNeeded() method to expand other preferences like other minimal items quantity
                if (!ctx.LogicSettings.MinimalStops || (ctx.LogicSettings.MinimalStops && (
                    25 > ctx.Inventory.GetItemAmountByType(POGOProtos.Inventory.Item.ItemId.ItemPokeBall).Result + ctx.Inventory.GetItemAmountByType(POGOProtos.Inventory.Item.ItemId.ItemGreatBall).Result + ctx.Inventory.GetItemAmountByType(POGOProtos.Inventory.Item.ItemId.ItemUltraBall).Result
                ))) {
                    var fortSearch = await ctx.Client.Fort.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                    if (fortSearch.ExperienceAwarded > 0)
                    {
                        machine.Fire(new FortUsedEvent
                        {
                            Exp = fortSearch.ExperienceAwarded,
                            Gems = fortSearch.GemsAwarded,
                            Items = StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch.ItemsAwarded)
                        });
                    }
                    if (++stopsHit % 5 == 0 && fortSearch.ItemsAwarded.Count > 0)
                    {
                        var refreshCachedInventory = await ctx.Inventory.RefreshCachedInventory();
                    }
                } else {
                    Logger.Write("Skipped " + fortInfo.Name);
                }

                await Task.Delay(1000);
                if (stopsHit % 5 == 0) //TODO: OR item/pokemon bag is full
                {
                    stopsHit = 0;
                    await RenamePokemonTask.Execute(ctx, machine);
                    await RecycleItemsTask.Execute(ctx, machine);
                    if (ctx.LogicSettings.EvolveAllPokemonWithEnoughCandy || ctx.LogicSettings.EvolveAllPokemonAboveIv)
                    {
                        await EvolvePokemonTask.Execute(ctx, machine);
                    }
                    if (ctx.LogicSettings.TransferDuplicatePokemon)
                    {
                        await TransferDuplicatePokemonTask.Execute(ctx, machine);
                    }
                }
            }
        }

        private static async Task<List<FortData>> GetPokeStops(Context ctx)
        {
            var mapObjects = await ctx.Client.Map.GetMapObjects();

            // Wasn't sure how to make this pretty. Edit as needed.
            var pokeStops = mapObjects.MapCells.SelectMany(i => i.Forts)
                .Where(
                    i =>
                        i.Type == FortType.Checkpoint &&
                        i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime() &&
                        ( // Make sure PokeStop is within max travel distance, unless it's set to 0.
                            LocationUtils.CalculateDistanceInMeters(
                                ctx.Settings.DefaultLatitude, ctx.Settings.DefaultLongitude,
                                i.Latitude, i.Longitude) < ctx.LogicSettings.MaxTravelDistanceInMeters) ||
                        ctx.LogicSettings.MaxTravelDistanceInMeters == 0
                );

            return pokeStops.ToList();
        }
    }
}