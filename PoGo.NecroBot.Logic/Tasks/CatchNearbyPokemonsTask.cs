﻿#region using directives

using System.Linq;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Logging;
using PoGo.NecroBot.Logic.State;
using PoGo.NecroBot.Logic.Utils;
using POGOProtos.Map.Pokemon;
using POGOProtos.Networking.Responses;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class CatchNearbyPokemonsTask
    {
        public static async Task Execute(Session session, StateMachine machine)
        {
            Logger.Write(session.Translations.GetTranslation(Common.TranslationString.LookingForPokemon), LogLevel.Debug);
            Logger.Write("Current Player location Latitude:" + session.Client.CurrentLatitude + ":CurrentLng: " + session.Client.CurrentLongitude + ":Altitude:" + session.Client.CurrentAltitude, LogLevel.Info);

            var pokemons = await GetNearbyPokemons(session);
            foreach (var pokemon in pokemons)
            {
                if (session.LogicSettings.UsePokemonToNotCatchFilter &&
                    session.LogicSettings.PokemonsNotToCatch.Contains(pokemon.PokemonId))
                {
                    Logger.Write(session.Translations.GetTranslation(Common.TranslationString.PokemonSkipped, pokemon.PokemonId));
                    continue;
                }

                var distance = LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude,
                    session.Client.CurrentLongitude, pokemon.Latitude, pokemon.Longitude);
                await Task.Delay(distance > 100 ? 3000 : 500);

                var encounter = await session.Client.Encounter.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnPointId);

                if (encounter.Status == EncounterResponse.Types.Status.EncounterSuccess)
                {
                    await CatchPokemonTask.Execute(session, machine, encounter, pokemon);
                    Logger.Write("Pokemon Found location Latitude:" + pokemon.Latitude + ":Longitude: " + pokemon.Longitude + ":Altitude:" + session.Client.CurrentAltitude + " :info:" + pokemon, LogLevel.Farming);

                }
                else if (encounter.Status == EncounterResponse.Types.Status.PokemonInventoryFull)
                {
                    if (session.LogicSettings.TransferDuplicatePokemon)
                    {
                        machine.Fire(new WarnEvent {Message = session.Translations.GetTranslation(Common.TranslationString.InvFullTransferring)});
                        await TransferDuplicatePokemonTask.Execute(session, machine);
                    }
                    else
                        machine.Fire(new WarnEvent
                        {
                            Message = session.Translations.GetTranslation(Common.TranslationString.InvFullTransferManually)
                        });
                }
                else
                {
                    machine.Fire(new WarnEvent {Message = session.Translations.GetTranslation(Common.TranslationString.EncounterProblem, encounter.Status)});
                }

                // If pokemon is not last pokemon in list, create delay between catches, else keep moving.
                if (!Equals(pokemons.ElementAtOrDefault(pokemons.Count() - 1), pokemon))
                {
                    await Task.Delay(session.LogicSettings.DelayBetweenPokemonCatch);
                }
            }
        }

        private static async Task<IOrderedEnumerable<MapPokemon>> GetNearbyPokemons(Session session)
        {
            var mapObjects = await session.Client.Map.GetMapObjects();

            var pokemons = mapObjects.MapCells.SelectMany(i => i.CatchablePokemons)
                .OrderBy(
                    i =>
                        LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude, session.Client.CurrentLongitude,
                            i.Latitude, i.Longitude));

            return pokemons;
        }
    }
}