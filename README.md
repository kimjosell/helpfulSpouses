# Helpful Spouses (Stardew Valley Mod)

A Stardew Valley mod that makes your spouse smarter — instead of just standing around the house, they can follow you around the farm and automatically water your crops when you plant seeds.

## Instructions for Build and Use

Steps to build and run the mod:

1. Install [SMAPI](https://smapi.io/) (Stardew Modding API) following the instructions on their website
2. Clone or download this repository into a folder
3. Open `stardew.sln` in Visual Studio and build the solution (Ctrl+Shift+B)
4. Copy the output `stardew.dll` and `manifest.json` into a new folder inside `Stardew Valley/Mods/HelpfulSpouses/`
5. Launch Stardew Valley through SMAPI

Instructions for using the mod in-game:

1. Start a save file that has a married spouse
2. Walk up to your spouse and press **G** to activate Follow Mode — they will start following you
3. While your spouse is following you, go to the farm and plant seeds on tilled soil — your spouse will automatically switch to Watering Mode and water the crops
4. Press **G** again near your spouse to send them back home

## Development Environment

To recreate the development environment, you need the following software and/or libraries with the specified versions:

* [Visual Studio Community 2022+](https://visualstudio.microsoft.com/) with .NET desktop development workload
* [.NET 6 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/6.0)
* [SMAPI 4.0.0+](https://smapi.io/) — provides the modding API and event system
* [HarmonyLib 2.x](https://github.com/pardeike/Harmony) — used to patch internal game methods
* Stardew Valley (purchased on Steam or GOG) — game DLLs are referenced locally

## Useful Websites to Learn More

I found these websites useful in developing this software:

* [SMAPI Modding Wiki](https://stardewvalleywiki.com/Modding:Index) — the main reference for everything Stardew modding
* [SMAPI API Docs](https://smapi.io/docs) — full list of events, helpers, and utilities
* [Harmony Documentation](https://harmony.pardeike.net/articles/intro.html) — how to patch game methods without editing source code
* [Stardew Valley Decompiled Source](https://github.com/veywrn/StardewValley) — helpful for understanding internal game logic
* [SMAPI Discord](https://discord.gg/stardewvalley) — active community for mod development help

## Future Work

The following items I plan to fix, improve, and/or add to this project in the future:

* [ ] Add a mini-menu when interacting with spouse in Follow Mode (work, water, free)
* [ ] Implement Working Mode — spouse helps with farm chores beyond watering
* [ ] Make the spouse return to Following Mode automatically after finishing watering
* [ ] Handle edge cases when the player warps while the spouse is watering
* [ ] Add config file support so players can customize the follow keybind and behavior
