// Game mode ship wizard maths, with no DOM: shared by the Game mode tab (/arena/me#gamemode) and node tests.
// GameModeShips.settings(ships, specs) turns the Ships panel's answers (roles, pace, items, upgrades,
// greens, starting items, tweaks) into Section:Key -> value strings, clamped to each setting's spec.
// Spec: docs/superpowers/specs/2026-11-27-game-mode-phase1-ships-design.md.
"use strict";

(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.GameModeShips = api;
})(typeof self !== "undefined" ? self : this, () => {
  const SHIPS = ["Warbird", "Javelin", "Spider", "Leviathan", "Terrier", "Weasel", "Lancaster", "Shark"];
  const PACES = ["slow", "steady", "standard", "fast", "mayhem"];
  const ITEMS = ["none", "few", "standard", "lots", "loads"];
  const UPGRADES = ["off", "slow", "standard", "fast"];
  const START_ITEMS = ["Repel", "Burst", "Decoy", "Thor", "Brick", "Rocket", "Portal"];

  // Stock values: SubspaceServer Zone/conf/svs (ship-*.conf without Soccer*, [Prize] in misc, prizeweights),
  // which the arena template includes. tests/js/gamemode-ships.test.js checks this copy against those files.
  const STANDARD = {
    Warbird: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":25,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":100,"StealthEnergy":333,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":300,"MaximumThrust":19,"MaximumSpeed":3250,"MaximumRecharge":1150,"MaximumEnergy":1700,"InitialRotation":210,"InitialThrust":16,"InitialSpeed":2010,"InitialRecharge":400,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":0,"InitialPortal":0,"BombBounceCount":0,"CloakStatus":0,"StealthStatus":1,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":8,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":2,"InitialGuns":1,"InitialBombs":0,"EmpBomb":0,"SeeMines":0,"DoubleBarrel":0},
    Javelin: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":25,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":100,"StealthEnergy":333,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":230,"MaximumThrust":17,"MaximumSpeed":3750,"MaximumRecharge":1150,"MaximumEnergy":1700,"InitialRotation":200,"InitialThrust":15,"InitialSpeed":2200,"InitialRecharge":400,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":0,"InitialPortal":0,"BombBounceCount":0,"CloakStatus":0,"StealthStatus":1,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":8,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":2,"InitialGuns":1,"InitialBombs":0,"EmpBomb":0,"SeeMines":0,"DoubleBarrel":0},
    Spider: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":25,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":100,"StealthEnergy":333,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":230,"MaximumThrust":17,"MaximumSpeed":3250,"MaximumRecharge":1150,"MaximumEnergy":1700,"InitialRotation":200,"InitialThrust":15,"InitialSpeed":2010,"InitialRecharge":500,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":1,"InitialPortal":0,"BombBounceCount":0,"CloakStatus":1,"StealthStatus":2,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":8,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":2,"InitialGuns":1,"InitialBombs":0,"EmpBomb":0,"SeeMines":0,"DoubleBarrel":0},
    Leviathan: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":25,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":100,"StealthEnergy":333,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":230,"MaximumThrust":17,"MaximumSpeed":3250,"MaximumRecharge":1150,"MaximumEnergy":1700,"InitialRotation":200,"InitialThrust":15,"InitialSpeed":2010,"InitialRecharge":400,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":0,"InitialPortal":0,"BombBounceCount":0,"CloakStatus":0,"StealthStatus":1,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":8,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":3,"InitialGuns":1,"InitialBombs":1,"EmpBomb":0,"SeeMines":0,"DoubleBarrel":0},
    Terrier: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":30,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":100,"StealthEnergy":333,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":230,"MaximumThrust":17,"MaximumSpeed":3250,"MaximumRecharge":1150,"MaximumEnergy":1700,"InitialRotation":200,"InitialThrust":15,"InitialSpeed":2010,"InitialRecharge":400,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":0,"InitialPortal":0,"BombBounceCount":0,"CloakStatus":0,"StealthStatus":1,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":8,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":2,"InitialGuns":2,"InitialBombs":0,"EmpBomb":0,"SeeMines":0,"DoubleBarrel":1},
    Weasel: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":25,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":100,"StealthEnergy":333,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":230,"MaximumThrust":17,"MaximumSpeed":3250,"MaximumRecharge":1150,"MaximumEnergy":1700,"InitialRotation":200,"InitialThrust":15,"InitialSpeed":2010,"InitialRecharge":400,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":0,"InitialPortal":0,"BombBounceCount":0,"CloakStatus":0,"StealthStatus":1,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":0,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":2,"InitialGuns":1,"InitialBombs":1,"EmpBomb":1,"SeeMines":1,"DoubleBarrel":0},
    Lancaster: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":25,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":100,"StealthEnergy":333,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":230,"MaximumThrust":17,"MaximumSpeed":3250,"MaximumRecharge":1150,"MaximumEnergy":1700,"InitialRotation":200,"InitialThrust":15,"InitialSpeed":2010,"InitialRecharge":400,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":0,"InitialPortal":0,"BombBounceCount":1,"CloakStatus":0,"StealthStatus":1,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":8,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":2,"InitialGuns":1,"InitialBombs":0,"EmpBomb":0,"SeeMines":0,"DoubleBarrel":0},
    Shark: {"SuperTime":6000,"ShieldsTime":4000,"Gravity":1500,"GravityTopSpeed":100,"BulletFireEnergy":20,"MultiFireEnergy":30,"BombFireEnergy":300,"BombFireEnergyUpgrade":50,"LandmineFireEnergy":270,"LandmineFireEnergyUpgrade":150,"BulletSpeed":2000,"BombSpeed":2000,"BulletFireDelay":25,"MultiFireDelay":50,"BombFireDelay":150,"LandmineFireDelay":125,"MultiFireAngle":500,"CloakEnergy":300,"StealthEnergy":300,"AntiWarpEnergy":100,"XRadarEnergy":250,"MaximumRotation":230,"MaximumThrust":17,"MaximumSpeed":3250,"MaximumRecharge":1150,"MaximumEnergy":1750,"InitialRotation":200,"InitialThrust":15,"InitialSpeed":2010,"InitialRecharge":400,"InitialEnergy":1000,"UpgradeRotation":40,"UpgradeThrust":2,"UpgradeSpeed":250,"UpgradeRecharge":166,"UpgradeEnergy":100,"AfterburnerEnergy":1200,"BombThrust":400,"BurstSpeed":3000,"BurstShrapnel":24,"TurretThrustPenalty":1,"TurretSpeedPenalty":125,"TurretLimit":5,"RocketTime":1000,"InitialBounty":70,"MaxMines":5,"RepelMax":3,"BurstMax":3,"DecoyMax":3,"ThorMax":3,"BrickMax":3,"PortalMax":3,"RocketMax":3,"InitialRepel":0,"InitialBurst":0,"InitialBrick":0,"InitialRocket":0,"InitialThor":0,"InitialDecoy":0,"InitialPortal":0,"BombBounceCount":0,"CloakStatus":1,"StealthStatus":1,"XRadarStatus":1,"AntiWarpStatus":1,"DamageFactor":30,"PrizeShareLimit":100,"AttachBounty":90,"ShrapnelMax":8,"ShrapnelRate":2,"MaxGuns":3,"MaxBombs":2,"InitialGuns":1,"InitialBombs":0,"EmpBomb":0,"SeeMines":0,"DoubleBarrel":0},
    Prize: {"PrizeFactor":1300,"PrizeMaxExist":8000,"PrizeMinExist":4000,"MultiPrizeCount":10,"PrizeDelay":300,"UpgradeVirtual":6},
    PrizeWeight: {"QuickCharge":80,"Energy":110,"Rotation":50,"Stealth":25,"Cloak":30,"AntiWarp":30,"XRadar":30,"Warp":10,"Gun":50,"Bomb":45,"BouncingBullets":45,"Thruster":50,"TopSpeed":80,"Recharge":30,"MultiFire":40,"Proximity":45,"Glue":0,"AllWeapons":5,"Shields":5,"Shrapnel":60,"Repel":20,"Burst":10,"Decoy":25,"Thor":20,"Portal":25,"Brick":10,"Rocket":25,"MultiPrize":80},
  };

  const ROLES = [
    { id: "standard", name: "Standard", blurb: "the stock ship" },
    { id: "gunner", name: "Gunner", blurb: "L2→L3 guns, double barrel, quick fire" },
    { id: "bomber", name: "Bomber", blurb: "L3 bouncing bombs, more shrapnel and mines" },
    { id: "sniper", name: "Sniper", blurb: "very fast L3 bullets, slow fire" },
    { id: "cloaker", name: "Cloaker", blurb: "cloak and stealth on, no X-radar" },
    { id: "deployer", name: "Deployer", blurb: "turret slots, portals, bricks" },
    { id: "anchor", name: "Anchor", blurb: "turret slots, huge energy, antiwarp" },
    { id: "interceptor", name: "Interceptor", blurb: "fast turn and speed, rockets to start" },
    { id: "support", name: "Support", blurb: "repels, decoys, bricks, bursts" },
    { id: "flagrunner", name: "Flag runner", blurb: "fastest ship, cheap afterburner, weak guns" },
    { id: "brawler", name: "Brawler", blurb: "high energy, wide multifire, close range" },
  ];

  // On top of the stock ship, before pace. Every role but standard also sets AttachBounty 0 and
  // TurretLimit 0 unless listed here (only deployer and anchor carry turrets).
  const ROLE_VALUES = {
    standard: {},
    gunner: { InitialGuns: 2, MaxGuns: 3, DoubleBarrel: 1, BulletFireDelay: 18, InitialBombs: 0, MaxBombs: 1 },
    bomber: { InitialBombs: 1, MaxBombs: 3, BombBounceCount: 1, ShrapnelMax: 16, MaxMines: 6, BombFireEnergy: 400, MaximumSpeed: 3000, InitialSpeed: 1900 },
    sniper: { BulletSpeed: 5000, BulletFireDelay: 70, InitialGuns: 2, MaxGuns: 3, DoubleBarrel: 0, MaximumThrust: 14, InitialThrust: 12, XRadarStatus: 2 },
    cloaker: { CloakStatus: 2, StealthStatus: 2, XRadarStatus: 0, CloakEnergy: 50, StealthEnergy: 200, MaxGuns: 2 },
    deployer: { TurretLimit: 5, InitialPortal: 1, PortalMax: 3, InitialBrick: 1, BrickMax: 3, MaximumEnergy: 1900 },
    anchor: { TurretLimit: 5, MaximumEnergy: 2400, InitialEnergy: 1500, MaximumRecharge: 1500, AntiWarpStatus: 2, MaximumSpeed: 2800, InitialSpeed: 1800, InitialRepel: 1 },
    interceptor: { MaximumRotation: 350, InitialRotation: 260, MaximumSpeed: 3800, InitialRocket: 2, MaximumEnergy: 1400, InitialEnergy: 900 },
    support: { InitialRepel: 2, RepelMax: 4, InitialDecoy: 1, InitialBrick: 1, InitialBurst: 1 },
    flagrunner: { MaximumSpeed: 4200, InitialSpeed: 2600, MaximumThrust: 22, InitialThrust: 18, AfterburnerEnergy: 700, MaxGuns: 2, InitialGuns: 1 },
    brawler: { MaximumEnergy: 2200, InitialEnergy: 1400, MultiFireAngle: 900, MultiFireDelay: 30, BulletSpeed: 1400, BulletFireDelay: 20 },
  };

  const PACE_GROUPS = {
    speed: ["InitialSpeed", "MaximumSpeed", "InitialThrust", "MaximumThrust", "InitialRotation", "MaximumRotation"],
    delay: ["BulletFireDelay", "MultiFireDelay", "BombFireDelay", "LandmineFireDelay"],
    energy: ["BulletFireEnergy", "MultiFireEnergy", "BombFireEnergy", "LandmineFireEnergy"],
    recharge: ["InitialRecharge", "MaximumRecharge"],
  };
  const PACE = {
    slow: { speed: 0.75, delay: 1.8, energy: 1.4, recharge: 0.8, mines: -2 },
    steady: { speed: 0.9, delay: 1.3, energy: 1.15, recharge: 0.9, mines: -1 },
    standard: { speed: 1, delay: 1, energy: 1, recharge: 1, mines: 0 },
    fast: { speed: 1.15, delay: 0.75, energy: 0.85, recharge: 1.15, mines: 1 },
    mayhem: { speed: 1.3, delay: 0.5, energy: 0.6, recharge: 1.4, mines: 3 },
  };
  // null = keep the stock value
  const ITEM_LEVELS = {
    none: { PrizeFactor: 0, MultiPrizeCount: 0, cap: 0 },
    few: { PrizeFactor: 600, MultiPrizeCount: 5, cap: 1 },
    standard: { PrizeFactor: null, MultiPrizeCount: null, cap: null },
    lots: { PrizeFactor: 2200, MultiPrizeCount: 15, cap: 5 },
    loads: { PrizeFactor: 3500, MultiPrizeCount: 20, cap: 8 },
  };
  const UPGRADE_FACTOR = { off: 0, slow: 0.5, standard: 1, fast: 1.5 };
  const UPGRADE_AMOUNTS = ["UpgradeEnergy", "UpgradeRecharge", "UpgradeThrust", "UpgradeSpeed", "UpgradeRotation"];
  const UPGRADE_WEIGHTS = ["Gun", "Bomb", "Energy", "Recharge", "Thruster", "TopSpeed", "Rotation", "MultiFire",
    "BouncingBullets", "Proximity", "Shrapnel", "QuickCharge", "AllWeapons"];
  const INITIAL_TO_MAX = [["InitialSpeed", "MaximumSpeed"], ["InitialThrust", "MaximumThrust"], ["InitialRotation", "MaximumRotation"],
    ["InitialRecharge", "MaximumRecharge"], ["InitialEnergy", "MaximumEnergy"], ["InitialGuns", "MaxGuns"], ["InitialBombs", "MaxBombs"]];

  function defaults() {
    return {
      pace: "standard", items: "standard", upgrades: "standard", greens: true,
      start: Object.fromEntries(START_ITEMS.map((i) => [i, 0])),
      roles: { Warbird: "gunner", Javelin: "bomber", Spider: "interceptor", Leviathan: "anchor",
        Terrier: "deployer", Weasel: "cloaker", Lancaster: "sniper", Shark: "support" },
      tweaks: {},
    };
  }

  /** A number that fits the spec: rounded; bools 0/1; ints within min/max when given. */
  function clamp(value, spec) {
    let v = Math.round(Number(value));
    if (!Number.isFinite(v)) v = 0;
    if (spec && spec.type === "bool") return v ? 1 : 0;
    if (spec && spec.min != null && v < spec.min) v = spec.min;
    if (spec && spec.max != null && v > spec.max) v = spec.max;
    return v;
  }

  const pick = (list, value, fallback) => (list.includes(value) ? value : fallback);

  function shipValues(ship, d) {
    const v = { ...STANDARD[ship] };
    const role = ROLE_VALUES[d.roles?.[ship]] ? d.roles[ship] : "standard";
    if (role !== "standard") { v.AttachBounty = 0; v.TurretLimit = 0; }
    Object.assign(v, ROLE_VALUES[role]);
    const roleInitial = Object.fromEntries(START_ITEMS.map((i) => [i, v[`Initial${i}`] ?? 0]));

    const p = PACE[pick(PACES, d.pace, "standard")];
    for (const [group, keys] of Object.entries(PACE_GROUPS)) for (const k of keys) if (k in v) v[k] = Math.round(v[k] * p[group]);
    if ("MaxMines" in v) v.MaxMines = Math.max(0, v.MaxMines + p.mines);

    const items = ITEM_LEVELS[pick(ITEMS, d.items, "standard")];
    if (items.cap != null) for (const i of START_ITEMS) v[`${i}Max`] = items.cap;

    const up = pick(UPGRADES, d.upgrades, "standard");
    if (up === "off") for (const [ini, max] of INITIAL_TO_MAX) { if (max in v) v[ini] = v[max]; }
    else for (const k of UPGRADE_AMOUNTS) if (k in v) v[k] = Math.round(v[k] * UPGRADE_FACTOR[up]);

    for (const i of START_ITEMS) {
      const want = Math.max(roleInitial[i], clamp(d.start?.[i] ?? 0, { min: 0, max: 3 }));
      if (roleInitial[i] > v[`${i}Max`]) v[`${i}Max`] = roleInitial[i];
      v[`Initial${i}`] = Math.min(want, v[`${i}Max`]);
    }
    return v;
  }

  /** The game mode's ship and prize settings: { "Section:Key": "value" } for every key with a spec. */
  function settings(d, specs) {
    d = d || defaults();
    const all = {};
    for (const ship of SHIPS) for (const [k, val] of Object.entries(shipValues(ship, d))) all[`${ship}:${k}`] = val;

    const items = ITEM_LEVELS[pick(ITEMS, d.items, "standard")];
    for (const [k, val] of Object.entries(STANDARD.Prize)) all[`Prize:${k}`] = items[k] != null ? items[k] : val;
    if (d.greens === false) all["Prize:PrizeFactor"] = 0;
    const f = UPGRADE_FACTOR[pick(UPGRADES, d.upgrades, "standard")];
    for (const [k, val] of Object.entries(STANDARD.PrizeWeight)) all[`PrizeWeight:${k}`] = UPGRADE_WEIGHTS.includes(k) ? Math.round(val * f) : val;

    for (const [name, raw] of Object.entries(d.tweaks || {})) {
      if (name in all && raw !== "" && raw != null && Number.isFinite(Number(raw))) all[name] = Number(raw);
    }

    const out = {};
    for (const [name, val] of Object.entries(all)) if (specs && specs[name]) out[name] = String(clamp(val, specs[name]));
    // A tweak or a clamp can leave a start above its maximum: the maximum wins.
    const pairs = [...INITIAL_TO_MAX, ...START_ITEMS.map((i) => [`Initial${i}`, `${i}Max`])];
    for (const ship of SHIPS) for (const [ini, max] of pairs) {
      const a = `${ship}:${ini}`, b = `${ship}:${max}`;
      if (a in out && b in out && +out[a] > +out[b]) out[a] = out[b];
    }
    return out;
  }

  /** How many tweaks the game mode has on one ship. */
  function tweakCount(d, ship) {
    return Object.keys(d?.tweaks || {}).filter((k) => k.startsWith(`${ship}:`)).length;
  }

  return { SHIPS, ROLES, PACES, ITEMS, UPGRADES, START_ITEMS, STANDARD, defaults, settings, clamp, tweakCount };
});
