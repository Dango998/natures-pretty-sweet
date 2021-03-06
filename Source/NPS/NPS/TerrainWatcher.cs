﻿using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.Noise;


namespace TKKN_NPS
{
	public class Watcher : MapComponent
	{
		//used to save data about active springs.
		public Dictionary<int, springData> activeSprings = new Dictionary<int, springData>();

		//used by weather
		public bool regenCellLists = true;
		public Dictionary<IntVec3, cellData> cellWeatherAffects = new Dictionary<IntVec3, cellData>();

		//rebuild every save to keep file size down
		public List<List<IntVec3>> tideCellsList = new List<List<IntVec3>>();
		public List<List<IntVec3>> floodCellsList = new List<List<IntVec3>>();
		public HashSet<IntVec3> lavaCellsList = new HashSet<IntVec3>();

		public int floodLevel = 0; // 0 - 3
		public int floodThreat = 0;
		public int tideLevel = 0; // 0 - 13
		public bool doCoast = true; //false if no coast

		public int howManyTideSteps = 13;
		public int howManyFloodSteps = 5;
		public bool bugFixFrostIsRemoved = false;

		public int ticks = 0;
		public Thing overlay;
		public int cycleIndex;
		public int MaxPuddles = 50;
		public int totalPuddles = 0;

		public Dictionary<string, Graphic> graphicHolder = new Dictionary<string, Graphic>();
		public float[] frostGrid;

		public ModuleBase frostNoise;

//		public Map mapRef;

		/* STANDARD STUFF */
		public Watcher(Map map) : base(map)
		{
			
		}

		public override void MapComponentTick()
		{
			this.ticks++;
			base.MapComponentTick();
			//run through saved terrain and check it
			this.checkThingsforLava();
			//run through pawns and effect them
			this.checkPawns();

			//environmental changes
			if (Settings.doWeather)
			{
				// this.checkRandomTerrain(); triggering on atmosphere affects
				this.doTides();
				this.doFloods();
			}
		}

	
		public override void ExposeData()
		{
			base.ExposeData();

			Scribe_Values.Look<bool>(ref this.regenCellLists, "regenCellLists", true, true);

			Scribe_Collections.Look<int, springData>(ref this.activeSprings, "TKKN_activeSprings", LookMode.Value, LookMode.Deep);
			Scribe_Collections.Look<IntVec3, cellData>(ref this.cellWeatherAffects, "cellWeatherAffects", LookMode.Value, LookMode.Deep);

			Scribe_Collections.Look<IntVec3>(ref this.lavaCellsList, "lavaCellsList", LookMode.Value);

			Scribe_Values.Look<bool>(ref this.doCoast, "doCoast", true, true);
			Scribe_Values.Look<int>(ref this.floodThreat, "floodThreat", 0, true);
			Scribe_Values.Look<int>(ref this.tideLevel, "tideLevel", 0, true);
			Scribe_Values.Look<int>(ref this.ticks, "ticks", 0, true);
			Scribe_Values.Look<int>(ref this.totalPuddles, "totalPuddles", this.totalPuddles, true);
			Scribe_Values.Look<bool>(ref this.bugFixFrostIsRemoved, "bugFixFrostIsRemoved", this.bugFixFrostIsRemoved, true);

		}



		public override void FinalizeInit()
		{
			
			base.FinalizeInit();
			this.rebuildCellLists();
			// this.map.GetComponent<FrostGrid>().Regenerate(); 
			if (TKKN_Holder.modsPatched.ToArray().Count() > 0)
			{
				Log.Message("TKKN NPS: Loaded patches for: " + string.Join(", ", TKKN_Holder.modsPatched.ToArray()));
			}
		}
		

		#region runs on setup
		public void rebuildCellLists()
		{

			if (Settings.regenCells == true)
			{
				this.regenCellLists = Settings.regenCells;
			}


			/*
			#region devonly
			this.regenCellLists = true;
			Log.Error("DEV STUFF IS ON");
			this.cellWeatherAffects = new Dictionary<IntVec3, cellData>();
			#endregion
			*/

			if (this.regenCellLists)
			{
				//spawn oasis. Do before cell list building so it's stored correctly.
				this.spawnOasis();
				this.fixLava();

				Rot4 rot = Find.World.CoastDirectionAt(map.Tile);

				IEnumerable<IntVec3> tmpTerrain = map.AllCells.InRandomOrder(); //random so we can spawn plants and stuff in this step.
				this.cellWeatherAffects = new Dictionary<IntVec3, cellData>();
				foreach (IntVec3 cell in tmpTerrain)
				{
					TerrainDef terrain = cell.GetTerrain(map);

					if (!cell.InBounds(map))
					{
						continue;
					}

					cellData thiscell = new cellData();
					thiscell.location = cell;
					thiscell.baseTerrain = terrain;


					if (terrain.defName == "TKKN_Lava")
					{
						//fix for lava pathing. If lava is near not!lava, make it impassable.
						bool edgeLava = false;
						int num = GenRadial.NumCellsInRadius(1);
						for (int i = 0; i < num; i++)
						{
							IntVec3 lavaCheck = cell + GenRadial.RadialPattern[i];
							if (lavaCheck.InBounds(map))
							{
								TerrainDef lavaCheckTerrain = lavaCheck.GetTerrain(this.map);
								if (lavaCheckTerrain.defName != "TKKN_Lava" && lavaCheckTerrain.defName != "TKKN_LavaDeep")
								{
									edgeLava = true;
								}
							}
						}
						if (!edgeLava)
						{
							this.map.terrainGrid.SetTerrain(cell, TerrainDefOf.TKKN_LavaDeep);
						}
					}
					else if (rot.IsValid && (terrain.defName == "Sand" || terrain.defName == "TKKN_SandBeachWetSalt"))
					{
						//get all the sand pieces that are touching water.
						for (int j = 0; j < this.howManyTideSteps; j++)
						{
							IntVec3 waterCheck = this.adjustForRotation(rot, cell, j);
							if (waterCheck.InBounds(map) && waterCheck.GetTerrain(map).defName == "WaterOceanShallow")
							{
								this.map.terrainGrid.SetTerrain(cell, TerrainDefOf.TKKN_SandBeachWetSalt);
								thiscell.tideLevel = j;
								break;
							}
						}
					}
					else if (terrain.HasTag("Water") && terrain.defName != "WaterOceanShallow" && terrain.defName != "WaterOceanDeep")
					{
						for (int j = 0; j < this.howManyFloodSteps; j++)
						{
							int num = GenRadial.NumCellsInRadius(j);
							for (int i = 0; i < num; i++)
							{
								IntVec3 bankCheck = cell + GenRadial.RadialPattern[i];
								if (bankCheck.InBounds(map))
								{
									TerrainDef bankCheckTerrain = bankCheck.GetTerrain(this.map);
									if (!bankCheckTerrain.HasTag("Water") && terrain.defName != "TKKN_SandBeachWetSalt")// || ((terrain.defName == "WaterDeep" || terrain.defName == "WaterDeep" || terrain.defName == "WaterMovingDeep") && bankCheckTerrain.defName != terrain.defName))
									{
										//see if this cell has already been done, because we can have each cell in multiple flood levels.
										cellData bankCell;
										if (this.cellWeatherAffects.ContainsKey(bankCheck))
										{
											bankCell = this.cellWeatherAffects[bankCheck];
										}
										else
										{
											bankCell = new cellData();
											bankCell.location = bankCheck;
											bankCell.baseTerrain = bankCheckTerrain;
										}
										bankCell.floodLevel.Add(j);
									}
								}
							}
						}

					}

					//Spawn special elements:
					this.spawnSpecialElements(cell);
					this.spawnSpecialPlants(cell);

					this.cellWeatherAffects[cell] = thiscell;
				}

			}


			//rebuild lookup lists.
			this.lavaCellsList = new HashSet<IntVec3>();
			this.tideCellsList = new List<List<IntVec3>>();
			this.floodCellsList = new List<List<IntVec3>>();

			for (int k = 0; k < this.howManyTideSteps; k++)
			{
				this.tideCellsList.Add(new List<IntVec3>());
			}
			for (int k = 0; k < this.howManyFloodSteps; k++)
			{
				this.floodCellsList.Add(new List<IntVec3>());
			}

			FrostGrid frostGrid = map.GetComponent<FrostGrid>();

			foreach (KeyValuePair<IntVec3, cellData> thiscell in cellWeatherAffects)
			{
				cellWeatherAffects[thiscell.Key].map = this.map;
				if (!bugFixFrostIsRemoved)
				{
					thiscell.Value.doFrostOverlay("remove");
				}
				//temp fix until I can figure out why regenerate wasn't working
//				frostGrid.SetDepth(thiscell.Value.location, 0);
				frostGrid.SetDepth(thiscell.Value.location, thiscell.Value.frostLevel);

				if (thiscell.Value.baseTerrain.defName == "TKKN_ColdSprings") {
					thiscell.Value.baseTerrain = TerrainDefOf.TKKN_ColdSpringsWater;
				}
				if (thiscell.Value.baseTerrain.defName == "TKKN_HotSprings")
				{
					thiscell.Value.baseTerrain = TerrainDefOf.TKKN_HotSpringsWater;
				}
				if (thiscell.Value.tideLevel > -1)
				{
					this.tideCellsList[thiscell.Value.tideLevel].Add(thiscell.Key);
				}
				if (thiscell.Value.floodLevel.Count != 0)
				{
					foreach (int level in thiscell.Value.floodLevel) {
						this.floodCellsList[level].Add(thiscell.Key);
					}
				}
				if (thiscell.Value.baseTerrain.HasTag("Lava")) {
					//future me: to do: split lava actions into ones that will affect pawns and ones that won't, since pawns can't walk on deep lava
					this.lavaCellsList.Add(thiscell.Key);
				}
			}
			bugFixFrostIsRemoved = true;

			if (this.regenCellLists)
			{
				this.setUpTidesBanks();
				this.regenCellLists = false;
			}



		}

		public void spawnSpecialPlants(IntVec3 c)
		{
			List<ThingDef> list = new List<ThingDef>()
			{
				ThingDef.Named("TKKN_SaltCrystal"),
			};

			TerrainDef terrain = c.GetTerrain(map);
			if (terrain.defName == "TKKN_SaltField" || terrain.defName == "TKKN_SandBeachWetSalt") {
				if (c.GetEdifice(map) == null && c.GetCover(map) == null && Rand.Value < .003f)
				{
					ThingDef thingDef = ThingDef.Named("TKKN_SaltCrystal");
					Plant plant = (Plant)ThingMaker.MakeThing(thingDef, null);
					plant.Growth = Rand.Range(0.07f, 1f);
					if (plant.def.plant.LimitedLifespan)
					{
						plant.Age = Rand.Range(0, Mathf.Max(plant.def.plant.LifespanTicks - 50, 0));
					}

					GenSpawn.Spawn(plant, c, map);
				}
			}

		}

		public void spawnSpecialElements(IntVec3 c)
		{
			TerrainDef terrain = c.GetTerrain(map);
			foreach (ElementSpawnDef element in DefDatabase<ElementSpawnDef>.AllDefs)
			{
				bool canSpawn = true;
				foreach (string biome in element.forbiddenBiomes)
				{
					if (map.Biome.defName == biome)
					{
						canSpawn = false;
						break;
					}
				}

				
				foreach (string biome in element.allowedBiomes)
				{
					if (map.Biome.defName != biome)
					{
						canSpawn = false;
						break;
					}
				}
				if (!canSpawn)
				{
					continue;
				}
//				Log.Error(element.thingDef.defName + " " +map.Biome.defName);


				foreach (string allowed in element.terrainValidationAllowed)
				{
					if (terrain.defName == allowed)
					{
						canSpawn = true;
						break;
					}
				}
				foreach (string notAllowed in element.terrainValidationAllowed)
				{
					if (terrain.HasTag(notAllowed))
					{
						canSpawn = false;
						break;
					}
				}


				if (canSpawn && Rand.Value < .0001f)
				{


					Thing thing = (Thing)ThingMaker.MakeThing(element.thingDef, null);
					GenSpawn.Spawn(thing, c, map);
				}

			}
		}

		public void spawnOasis()
		{
			if (this.map.Biome.defName == "TKKN_Oasis")
			{
				//spawn a big ol cold spring
				IntVec3 springSpot = CellFinderLoose.TryFindCentralCell(map, 10, 15, (IntVec3 x) => !x.Roofed(map));
				Spring spring = (Spring)ThingMaker.MakeThing(ThingDef.Named("TKKN_OasisSpring"), null);
				GenSpawn.Spawn(spring, springSpot, map);
			}
			if (Rand.Value < .001f)
			{
				this.spawnOasis();
			}
		}

		public void fixLava()
		{
			//set so the area people land in will most likely not be lava.
			if (this.map.Biome.defName == "TKKN_VolcanicFlow")
			{
				IntVec3 centerSpot = CellFinderLoose.TryFindCentralCell(map, 10, 15, (IntVec3 x) => !x.Roofed(map));
				int num = GenRadial.NumCellsInRadius(23);
				for (int i = 0; i < num; i++)
				{
					this.map.terrainGrid.SetTerrain(centerSpot + GenRadial.RadialPattern[i], TerrainDefOf.TKKN_LavaRock_RoughHewn);
				}

			}
		}

		public IntVec3 adjustForRotation(Rot4 rot, IntVec3 cell, int j)
		{
			IntVec3 newDirection = new IntVec3(cell.x, cell.y, cell.z);
			if (rot == Rot4.North)
			{
				newDirection.z += j + 1;
			}
			else if (rot == Rot4.South)
			{
				newDirection.z -= j - 1;
			}
			else if (rot == Rot4.East)
			{
				newDirection.x += j + 1;
			}
			else if (rot == Rot4.West)
			{
				newDirection.x -= j - 1;
			}
			return newDirection;
		}

		private void setUpTidesBanks()
		{
			//set up tides and river banks for the first time:
			if (this.doCoast)
			{
				//set up for low tide
				this.tideLevel = 0;

				for (int i = 0; i < this.howManyTideSteps; i++)
				{
					List<IntVec3> makeSand = this.tideCellsList[i];
					for (int j = 0; j < makeSand.Count; j++)
					{
						IntVec3 c = makeSand[j];
						cellData cell = this.cellWeatherAffects[c];
						cell.baseTerrain = TerrainDefOf.TKKN_SandBeachWetSalt;
						this.map.terrainGrid.SetTerrain(c, TerrainDefOf.TKKN_SandBeachWetSalt);
					}
				}
				//bring to current tide levels
				string tideLevel = getTideLevel();
				int max = 0;
				if (tideLevel == "normal")
				{
					max = (int)Math.Floor((this.howManyTideSteps - 1) / 2M);
				}
				else if (tideLevel == "high")
				{
					max = this.howManyTideSteps - 1;
				}
				for (int i = 0; i < max; i++)
				{
					List<IntVec3> makeSand = this.tideCellsList[i];
					for (int j = 0; j < makeSand.Count; j++)
					{
						IntVec3 c = makeSand[j];
						cellData cell = this.cellWeatherAffects[c];
						cell.setTerrain("tide");
					}
				}
				this.tideLevel = max;

			}

			string flood = getFloodType();

			for (int i = 0; i < this.howManyFloodSteps; i++)
			{
				List<IntVec3> makeWater = this.floodCellsList[i];
				for (int j = 0; j < makeWater.Count; j++)
				{
					IntVec3 c = makeWater[j];
					cellData cell = this.cellWeatherAffects[c];
					if (!cell.baseTerrain.HasTag("Water"))
					{
						cell.baseTerrain = TerrainDefOf.TKKN_RiverDeposit;
					}
					if (flood == "high")
					{
						cell.setTerrain("flooded");
					}
					else if (flood == "low")
					{
						cell.overrideType = "dry";
						cell.setTerrain("flooded");
					} else if (i < howManyFloodSteps/2) {
						cell.setTerrain("flooded");
					} else {
						cell.overrideType = "dry";
						cell.setTerrain("flooded");
					}

				}
			}

		}


		#endregion


		#region effect by terrain
		/*
		 * //MOVED TO STEADYATMOSPHEREEFFECTS
		public void checkRandomTerrain() {
			int num = Mathf.RoundToInt((float)this.map.Area * 0.0001f);
			int area = this.map.Area;
			for (int i = 0; i < num; i++)
			{
				if (this.cycleIndex >= area)
				{
					this.cycleIndex = 0;
				}
				IntVec3 c = this.map.cellsInRandomOrder.Get(this.cycleIndex);
				this.doCellEnvironment(c);

				this.cycleIndex++;
			}

		}
		*/

		public void doCellEnvironment(IntVec3 c)
		{
			if (!this.cellWeatherAffects.ContainsKey(c))
			{
				return;
			}
			cellData cell = this.cellWeatherAffects[c];

			TerrainDef currentTerrain = c.GetTerrain(this.map);
			Room room = c.GetRoom(this.map, RegionType.Set_All);
			bool roofed = this.map.roofGrid.Roofed(c);
			bool flag2 = room != null && room.UsesOutdoorTemperature;

			bool gettingWet = false;
			bool isMelt = false;

			//check if the terrain has been floored
			DesignationCategoryDef cats = currentTerrain.designationCategory;
			if (cats != null)
			{
				if (cats.defName == "Floors")
				{
					cell.baseTerrain = currentTerrain;
				}
			}

			//spawn special things
			if (Rand.Value < .0001f)
			{
				if (c.InBounds(this.map))
				{

					string defName = "";

					if (currentTerrain.defName == "TKKN_Lava")
					{
						defName = "TKKN_LavaRock";
					}
					else if (currentTerrain.defName == "TKKN_LavaRock_RoughHewn" && this.map.Biome.defName == "TKKN_VolcanicFlow")
					{
						defName = "TKKN_SteamVent";
					}

					if (defName != "")
					{
						Thing check = (Thing)(from t in c.GetThingList(this.map)
											  where t.def.defName == defName
											  select t).FirstOrDefault<Thing>();
						if (check == null)
						{
							Thing thing = (Thing)ThingMaker.MakeThing(ThingDef.Named(defName), null);
							GenSpawn.Spawn(thing, c, map);
						}
					}
				}
			}


			#region Rain

			if (Settings.showRain && !roofed && this.map.weatherManager.curWeather.rainRate > .001f)
			{
				if (this.floodThreat < 1090000) {
					this.floodThreat += 1 + 2 * (int)Math.Round(this.map.weatherManager.curWeather.rainRate);
				}
				gettingWet = true;
				cell.setTerrain("wet");
			}
			else
			{
				if (this.map.weatherManager.curWeather.rainRate == 0)
				{
					this.floodThreat--;
				}
				//DRY GROUND
				cell.setTerrain("dry");
			}
			#endregion

			#region Cold
			bool isCold = this.checkIfCold(c);
			if (isCold)
			{
				cell.setTerrain("frozen");
			}
			else
			{
				cell.setTerrain("thaw");
			}

			#region Frost

//			if (c.x == 140)
//			{
			//	Log.Error("Cell temp " + cell.temperature.ToString() + " frosty: " + (cell.temperature + -.025f).ToString() + " current depth" +this.map.GetComponent<FrostGrid>().GetDepth(c).ToString());
//			}
//			Log.Error(c.ToString() + " ...... roofed:" + roofed.ToString() + " isCold:" + isCold.ToString() + " temp: " + this.map.mapTemperature.OutdoorTemp.ToString());
			if (isCold)
			{
				//handle frost based on snowing
				if (!roofed && this.map.weatherManager.SnowRate > 0.001f)
				{
					this.map.GetComponent<FrostGrid>().AddDepth(c, this.map.weatherManager.SnowRate * -.01f);
				}
				else
				{
					CreepFrostAt(c, 0.46f * .3f, map);
				}
			}
			else
			{
				float frosty = cell.temperature  * -.025f;
//				float frosty = this.map.mapTemperature.OutdoorTemp * -.03f;
				this.map.GetComponent<FrostGrid>().AddDepth(c, frosty);
				if (this.map.GetComponent<FrostGrid>().GetDepth(c) > .3f) {
					// cell.isMelt = true;
				}
			}


			#endregion

			/* MAKE THIS A WEATHER
			#region heat
			Thing overlayHeat = (Thing)(from t in c.GetThingList(this.map)
										where t.def.defName == "TKKN_HeatWaver"
										select t).FirstOrDefault<Thing>();
			if (this.checkIfHot(c))
			{
				if (overlayHeat == null && Settings.showHot)
				{
					Thing heat = ThingMaker.MakeThing(ThingDefOf.TKKN_HeatWaver, null);
					GenSpawn.Spawn(heat, c, map);
				}
			}
			else
			{
				if (overlayHeat != null)
				{
					overlayHeat.Destroy();
				}
			}
			#endregion
			*/

			#region Puddles

			if (cell.howWet < 3 && Settings.showRain && (cell.isMelt || gettingWet))
			{
				cell.howWet +=2;				 
			}
			else if (cell.howWet > -1)
			{
				cell.howWet--;
			}


			//PUDDLES
			Thing puddle = (Thing)(from t in c.GetThingList(this.map)
								   where t.def.defName == "TKKN_FilthPuddle"
								   select t).FirstOrDefault<Thing>();

			if (cell.howWet == 3 && !isCold && this.MaxPuddles > this.totalPuddles && cell.currentTerrain.defName != "TKKN_SandBeachWetSalt")
			{
				if (puddle == null)
				{
					FilthMaker.MakeFilth(c, this.map, ThingDef.Named("TKKN_FilthPuddle"), 1);
					this.totalPuddles++;
				}
			}
			else if (cell.howWet <= 0 && puddle != null)
			{
				puddle.Destroy();
				this.totalPuddles--;
			}
			cell.isMelt = false;
			#endregion

			/*CELL SHOULD BE HANDLING THIS NOW:
			//since it changes, make sure the lava list is still good:

			if (currentTerrain.defName == "TKKN_Lava") {
				this.lavaCellsList.Add(c);
			} else {
				this.lavaCellsList.Remove(c);
			}
			*/

			this.cellWeatherAffects[c] = cell;
		}

		private bool checkIfCold(IntVec3 c)
		{
			if (!Settings.showCold) {
				return false;
			}
			if (!this.cellWeatherAffects.ContainsKey(c))
			{
				return false;
			}
			cellData cell = this.cellWeatherAffects[c];
			Room room = c.GetRoom(this.map, RegionType.Set_All);
			bool flag2 = room != null && room.UsesOutdoorTemperature;

			bool isCold = false;
			if (room == null || flag2)
			{
				
				cell.temperature = this.map.mapTemperature.OutdoorTemp;
				if (this.map.mapTemperature.OutdoorTemp < 0f)
				{
					isCold = true;
				}
			}
			if (room != null)
			{
				if (!flag2)
				{
					float temperature = room.Temperature;
					cell.temperature = temperature;
					if (temperature < 0f)
					{
						isCold = true;
					}
				}
			}

			return isCold;
		}

		private bool checkIfHot(IntVec3 c)
		{
			if (!Settings.showHot)
			{
				return false;
			}

			cellData cell = this.cellWeatherAffects[c];
			Room room = c.GetRoom(this.map, RegionType.Set_All);
			bool flag2 = room != null && room.UsesOutdoorTemperature;

			bool isHot = false;
			if (room == null || flag2)
			{
				cell.temperature = this.map.mapTemperature.OutdoorTemp;
				if (this.map.mapTemperature.OutdoorTemp > 37f)
				{
					isHot = true;
				}
			}
			if (room != null)
			{
				if (!flag2)
				{
					float temperature = room.Temperature;
					cell.temperature = temperature;
					if (temperature > 37f)
					{
						isHot = true;
					}
				}
			}

			return isHot;
		}

		public static void CreepFrostAt(IntVec3 c, float baseAmount, Map map)
		{
			if (map.GetComponent<Watcher>(). frostNoise == null)
			{
				map.GetComponent<Watcher>().frostNoise = new Perlin(0.039999999105930328, 2.0, 0.5, 5, Rand.Range(0, 651431), QualityMode.Medium);
			}
			float num = map.GetComponent<Watcher>().frostNoise.GetValue(c);
			num += 1f;
			num *= 0.5f;
			if (num < 0.5f)
			{
				num = 0.5f;
			}
			float depthToAdd = baseAmount * num;

			map.GetComponent<FrostGrid>().AddDepth(c, depthToAdd);
		}

		/*
		public static float MeltAmountAt(float temperature)
		{
			if (temperature < 0f)
			{
				return 0f;
			}
			if (temperature < 10f)
			{
				return temperature * temperature * 0.0058f * 0.1f;
			}
			return temperature * 0.0058f;
		}
		*/


		public string getFloodType()
		{
			string flood = "normal";
			Season season = GenLocalDate.Season(this.map);
			if (this.floodThreat > 1000000)
			{
				flood = "high";
			}
			else if (season.Label() == "spring")
			{
				flood = "high";
			}
			else if (season.Label() == "fall")
			{
				flood = "low";
			}
			return flood;
		}

		public void doFloods()
		{
			if (this.ticks % 300 != 0) {
				int half = (int)Math.Round((this.howManyFloodSteps - 1M) / 2);
				int max = this.howManyFloodSteps - 1;

				
				string flood = this.getFloodType();
				


				string overrideType = "";
				if (this.floodLevel < max && flood == "high")
				{
					overrideType = "wet";
				}
				else if (this.floodLevel > 0 && flood == "low")
				{
					overrideType = "dry";
				}
				else if (this.floodLevel < half && flood == "normal")
				{
					overrideType = "wet";
				}
				else if (this.floodLevel > half && flood == "normal")
				{
					overrideType = "dry";
				}

				if (this.floodLevel == this.howManyFloodSteps && flood == "high")
				{
					return;
				}
				else if (this.floodLevel == 0 && flood == "low")
				{
					return;
				}
				else if (this.floodLevel == half && flood == "normal")
				{
					return;
				}
				
				
				List<IntVec3> cellsToChange = this.floodCellsList[this.floodLevel];
				for (int i = 0; i < cellsToChange.Count; i++)
				{
					IntVec3 c = cellsToChange[i];
					cellData cell = this.cellWeatherAffects[c];
					if (overrideType != "")
					{
						cell.overrideType = overrideType;
					}
					cell.setTerrain("flooded");
				}

				if (this.floodLevel < max && flood == "high")
				{
					this.floodLevel++;
				}
				else if (this.floodLevel > 0 && flood == "low")
				{
					this.floodLevel--;
				}
				else if (this.floodLevel < half && flood == "normal")
				{
					this.floodLevel++;
				}
				else if (this.floodLevel > half && flood == "normal")
				{
					this.floodLevel--;
				}
			}
		}

		private string getTideLevel()
		{
			if (this.map.gameConditionManager.ConditionIsActive(GameConditionDefOf.Eclipse))
			{
				return "high";
			}
			else if (GenLocalDate.HourOfDay(this.map) > 4 && GenLocalDate.HourOfDay(this.map) < 8)
			{
				return "low";
			}
			else if (GenLocalDate.HourOfDay(this.map) > 15 && GenLocalDate.HourOfDay(this.map) < 20)
			{
				return "high";
			}
			return "normal";
		}

		private void doTides()
		{
			//nots to future me: use this.howManyTideSteps - 1 so we always have a little bit of wet sand, or else it looks stupid.
			if (!this.doCoast || !Settings.doTides || this.ticks % 100 != 0)
			{
				return;
			}

			string tideType = getTideLevel();
			int half = (int) Math.Round((this.howManyTideSteps - 1M) / 2);
			int max = this.howManyTideSteps - 1;

			if ((tideType == "normal" && tideLevel == half) || (tideType == "high" && tideLevel == max) || (tideType == "low" && tideLevel == 0))
			{
				return;
			}
			if (tideType == "normal" && tideLevel == max) {
				tideLevel--;
				return;
			}


			List < IntVec3> cellsToChange = this.tideCellsList[this.tideLevel];
			for (int i = 0; i < cellsToChange.Count; i++)
			{
				IntVec3 c = cellsToChange[i];
				cellData cell = this.cellWeatherAffects[c];
				cell.setTerrain("tide");
			}

			if (tideType == "high")
			{
				if (this.tideLevel < max )
				{
					this.tideLevel++;
				}
			}
			else if (tideType == "low")
			{
				if (this.tideLevel > 0)
				{
					this.tideLevel--;
				}
			}
			else if (tideType == "normal")
			{
				if (this.tideLevel > half)
				{
					this.tideLevel--;
				}
				else if (this.tideLevel < half)
				{
					this.tideLevel++;
				}
			}
		}

		#endregion

		#endregion
		#region effects by pawns
		public void checkThingsforLava()
		{

			HashSet<IntVec3> removeFromLava = new HashSet<IntVec3>();

			foreach (IntVec3 c in this.lavaCellsList)
			{
			

				cellData cell = this.cellWeatherAffects[c];
				
				//check to see if it's still lava. Ignore roughhewn because lava can freeze/rain will cool it.
				if (!c.GetTerrain(map).HasTag("Lava") && c.GetTerrain(map).defName != "TKKN_LavaRock_RoughHewn")
				{
					cell.baseTerrain = c.GetTerrain(map);
					removeFromLava.Add(c);
					continue;
				}



				List<Thing> things = c.GetThingList(this.map);
				for (int j = things.Count - 1; j >= 0; j--)
				{
					//thing.TryAttachFire(5);
					FireUtility.TryStartFireIn(c, this.map, 5f);

					Thing thing = things[j];
					float statValue = thing.GetStatValue(StatDefOf.Flammability, true);
					bool alt = thing.def.altitudeLayer == AltitudeLayer.Item;
					if (statValue == 0f && alt == true)
					{
						if (!thing.Destroyed && thing.def.destroyable)
						{
							thing.Destroy(DestroyMode.Vanish);
						}
					}
				}
			}

			foreach (IntVec3 c in removeFromLava)
			{
				this.lavaCellsList.Remove(c);
			}
			

			int n = 0;
			foreach (IntVec3 c in this.lavaCellsList.InRandomOrder())
			{

				GenTemperature.PushHeat(c, map, 1);

				if (n > 50)
				{
					break;
				}

				if (this.map.weatherManager.curWeather.rainRate > .0001f)
				{
					if (Rand.Value < .0009f)
					{
						MoteMaker.ThrowHeatGlow(c, this.map, 5f);
						MoteMaker.ThrowSmoke(c.ToVector3(), this.map, 4f);

					}
				}
				else
				{
					/*
					if (Rand.Value < .0001f)
					{

						MoteThrown moteThrown = (MoteThrown)ThingMaker.MakeThing(ThingDef.Named("TKKN_HeatWaver"), null);
						moteThrown.Scale = Rand.Range(2f, 4f) * 2;
						moteThrown.exactPosition = c.ToVector3();
						moteThrown.SetVelocity(Rand.Bool ? -90 : 90, 0.12f);
						GenSpawn.Spawn(moteThrown, c, this.map);
					}
					else 
					*/
					if (Rand.Value < .0005f)
					{
						MoteMaker.ThrowSmoke(c.ToVector3(), this.map, 4f);

					}
				}
				n++;
			}
		}
	

		private void checkPawns()
		{
			List<Pawn> pawns = this.map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < pawns.Count; i++)
			{
				Pawn pawn = pawns[i];
				if (pawn == null || !pawn.Spawned)
				{
					continue;
				}

				#region paths
				if (pawn.Position.InBounds(map))
				{
					//damage plants and remove snow/frost where they are. This will hopefully generate paths as pawns walk :)
					if (this.checkIfCold(pawn.Position))
					{
						map.GetComponent<FrostGrid>().AddDepth(pawn.Position, (float)-.05);
						map.snowGrid.AddDepth(pawn.Position, (float)-.05);
					}

					if (pawn.RaceProps.Humanlike)
					{
						List<Thing> things = pawn.Position.GetThingList(map);
						foreach (Thing thing in things.ToList())
						{
							if (thing.def.category == ThingCategory.Plant && thing.def.altitudeLayer == AltitudeLayer.LowPlant && thing.def.plant.harvestTag != "Standard")
							{
								thing.TakeDamage(new DamageInfo(DamageDefOf.Rotting, 99999, -.05f, null, null, null, DamageInfo.SourceCategory.ThingOrUnknown));
							}
						}
					}
				}
				#endregion

				TerrainDef terrain = pawn.Position.GetTerrain(map);
				if ((terrain.defName == "TKKN_SaltField" || terrain.defName == "TKKN_Salted_Earth") && pawn.def.defName == "TKKN_giantsnail")
				{
					this.burnSnails(pawn);
					continue;
				}
				if (this.ticks % 250 == 0 && (terrain.defName == "TKKN_HotSpringsWater" || terrain.defName == "TKKN_ColdSpringsWater"))
				{
					if (pawn.RaceProps.Humanlike && pawn.needs == null)
					{
						continue;
					}
					HediffDef hediffDef = new HediffDef();
					if (terrain.defName == "TKKN_HotSpringsWater")
					{
						if (pawn.needs.comfort != null)
						{
							pawn.needs.comfort.lastComfortUseTick--;
						}
						hediffDef = HediffDefOf.TKKN_hotspring_chill_out;
						if (pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef) == null)
						{
							Hediff hediff = HediffMaker.MakeHediff(hediffDef, pawn, null);
							pawn.health.AddHediff(hediff, null, null);
						}
					}
					if (terrain.defName == "TKKN_ColdSpringsWater")
					{
						pawn.needs.rest.CurLevel++;
						hediffDef = HediffDefOf.TKKN_coldspring_chill_out;
						if (pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef, false) == null)
						{
							Hediff hediff = HediffMaker.MakeHediff(hediffDef, pawn, null);
							pawn.health.AddHediff(hediff, null, null);
						}
					}
				}

				if (this.ticks % 150 != 0)
				{
					return;
				}

				bool isCold = this.checkIfCold(pawn.Position);
				if (isCold)
				{
					IntVec3 head = pawn.Position;
					head.z += 1;
					if (!head.ShouldSpawnMotesAt(map) || map.moteCounter.SaturatedLowPriority)
					{
						return;
					}
					MoteThrown moteThrown = (MoteThrown)ThingMaker.MakeThing(ThingDef.Named("TKKN_Mote_ColdBreath"), null);
					moteThrown.airTimeLeft = 99999f;
					moteThrown.Scale = Rand.Range(.5f, 1.5f);
					moteThrown.rotationRate = Rand.Range(-30f, 30f);
					moteThrown.exactPosition = head.ToVector3();
					moteThrown.SetVelocity((float)Rand.Range(20, 30), Rand.Range(0.5f, 0.7f));
					GenSpawn.Spawn(moteThrown, head, map);
				}
			}
		}

		private void burnSnails(Pawn pawn)
		{
			BattleLogEntry_DamageTaken battleLogEntry_DamageTaken = new BattleLogEntry_DamageTaken(pawn, RulePackDefOf.DamageEvent_Fire, null);
			Find.BattleLog.Add(battleLogEntry_DamageTaken);
			DamageInfo dinfo = new DamageInfo(DamageDefOf.Flame, 100, -1f, pawn, null, null, DamageInfo.SourceCategory.ThingOrUnknown);
			dinfo.SetBodyRegion(BodyPartHeight.Undefined, BodyPartDepth.Outside);
			pawn.TakeDamage(dinfo).InsertIntoLog(battleLogEntry_DamageTaken);
		}

		#endregion
	}
}
