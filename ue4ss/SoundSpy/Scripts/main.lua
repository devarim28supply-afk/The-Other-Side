-- SoundSpy: reads the sounds The Other Side plays straight from the game and writes a live feed
-- for the Other Side Sound overlay (ue4ss/Mods/SoundSpy/soundfeed.txt, emptied at every game start).
-- Lines (tab separated, UE units = cm):
--   L  x y z yaw                      listener = local camera (sent when it moves or turns)
--   S  id name x y z range src        a sound started (id 0 = one-shot at a point; range = hearing distance in m, -1 unknown)
--   M  id x y z                       a playing sound moved (ghost walking)
--   E  id                             it stopped
--   2  name src                       "in your head" sound, no position for anyone
-- src = ghost | player | own | other. Only the KIND of source is sent, never ghost model / type names.

local UEHelpers = require("UEHelpers")

local modDir = debug.getinfo(1, "S").source:match("^@(.*)[/\\]Scripts[/\\]")
local out = io.open((modDir or ".") .. "\\soundfeed.txt", "w")
local function emit(line) if out then out:write(line, "\n") end end
local function flush() if out then out:flush() end end

local function valid(o) return o ~= nil and o:IsValid() end
local function name(obj)
    if not valid(obj) then return "?" end
    local ok, s = pcall(function() return obj:GetFName():ToString() end)
    return ok and s or "?"
end
local function addr(o) return valid(o) and o:GetAddress() or 0 end

local pawnAddr = 0
local function localPawn()
    local pc = UEHelpers.GetPlayerController()
    if not valid(pc) then return nil, nil end
    return pc, pc.Pawn
end

-- what kind of thing made a sound: the ghost, another player, you, or anything else
local function sourceKind(actor)
    if not valid(actor) then return "other" end
    for _ = 1, 3 do                                   -- held items / attached props: walk up to the character
        if addr(actor) == pawnAddr and pawnAddr ~= 0 then return "own" end
        local cls = name(actor:GetClass())
        if cls:find("Ghost") then return "ghost" end
        if cls:find("Survivor") or cls:find("Character_") then return "player" end
        local up = actor:GetOwner()
        if not valid(up) then break end
        actor = up
    end
    return "other"
end
local ActorClass = nil
local function actorOf(o)
    if not valid(o) then return nil end
    if ActorClass == nil then ActorClass = StaticFindObject("/Script/Engine.Actor") end
    local ok, isActor = pcall(function() return o:IsA(ActorClass) end)
    if ok and isActor then return o end
    local ok2, ow = pcall(function() return o:GetOwner() end)
    return ok2 and ow or nil
end

-- hearing distance of a sound in metres (-1 unknown)
local function rangeOf(atten, sound)
    local a = (atten ~= nil and valid(atten)) and atten or nil
    if a == nil and valid(sound) then
        local ok, s = pcall(function() return sound.AttenuationSettings end)
        if ok and valid(s) then a = s end
    end
    if a == nil then return -1 end
    local ok, r = pcall(function()
        local at = a.Attenuation
        return (at.FalloffDistance + at.AttenuationShapeExtents.X) / 100
    end)
    return (ok and r) and r or -1
end

local function clean(s) return (s:gsub("[\t\n]", " ")) end

-- 1) one-shot sounds played at a point (ghost footsteps, door bangs, ghost events, players' footsteps)
RegisterHook("/Script/Engine.GameplayStatics:PlaySoundAtLocation", function(ctx, world, sound, loc, rot, vol, pitch, start, atten)
    pcall(function()
        local s = sound:get()
        if not valid(s) then return end
        if vol:get() <= 0 then return end
        local l = loc:get()
        emit(string.format("S\t0\t%s\t%.0f\t%.0f\t%.0f\t%.1f\t%s", clean(name(s)), l.X, l.Y, l.Z,
            rangeOf(atten:get(), s), sourceKind(actorOf(world:get()))))
        flush()
    end)
end)
-- 2) sounds with no position
local function hook2D(path)
    RegisterHook(path, function(ctx, world, sound)
        pcall(function()
            local s = sound:get()
            if not valid(s) then return end
            emit("2\t" .. clean(name(s)) .. "\t" .. sourceKind(actorOf(world:get())))
            flush()
        end)
    end)
end
hook2D("/Script/Engine.GameplayStatics:PlaySound2D")
hook2D("/Script/Engine.GameplayStatics:SpawnSound2D")

-- 3) every 100 ms: the listener, and every playing audio component (attached / spawned sounds, also from C++)
local seen = {}        -- id -> {x, y, z}
local lastL = nil
-- LoopInGameThreadWithDelay keeps ONE callback on the game thread. LoopAsync + ExecuteInGameThread made a new
-- callback every 100 ms and crashed the game after ~7 min (UE4SS "Ref was not function", 2026-10-16).
LoopInGameThreadWithDelay(100, function()
        pcall(function()
            local pc, pawn = localPawn()
            pawnAddr = addr(pawn)
            if valid(pc) and valid(pc.PlayerCameraManager) then
                local cl = pc.PlayerCameraManager:GetCameraLocation()
                local cr = pc.PlayerCameraManager:GetCameraRotation()
                if lastL == nil or math.abs(cl.X - lastL[1]) + math.abs(cl.Y - lastL[2]) + math.abs(cl.Z - lastL[3]) > 5
                   or math.abs(cr.Yaw - lastL[4]) > 1 then
                    lastL = { cl.X, cl.Y, cl.Z, cr.Yaw }
                    emit(string.format("L\t%.0f\t%.0f\t%.0f\t%.1f", cl.X, cl.Y, cl.Z, cr.Yaw))
                end
            end
            local now = {}
            local comps = FindAllOf("AudioComponent")
            if comps then
                for _, c in ipairs(comps) do
                    if c:IsValid() and c:IsPlaying() then
                        local id = tostring(c:GetAddress())
                        local prev = seen[id]
                        local p = nil
                        if prev == nil then
                            local s = c.Sound
                            local sname = clean(name(s))
                            if sname:find("^UDS_") then
                                -- weather beds follow the player everywhere: never sent, never tracked
                                prev = { skip = true }
                            else
                                p = c:K2_GetComponentLocation()
                                local atten = c.AttenuationSettings
                                emit(string.format("S\t%s\t%s\t%.0f\t%.0f\t%.0f\t%.1f\t%s", id, sname, p.X, p.Y, p.Z,
                                    rangeOf(valid(atten) and atten or nil, s), sourceKind(c:GetOwner())))
                                prev = { p.X, p.Y, p.Z }
                            end
                        elseif not prev.skip then p = c:K2_GetComponentLocation() end
                        if p ~= nil and prev[1] ~= nil and math.abs(p.X - prev[1]) + math.abs(p.Y - prev[2]) + math.abs(p.Z - prev[3]) > 30 then
                            emit(string.format("M\t%s\t%.0f\t%.0f\t%.0f", id, p.X, p.Y, p.Z))
                            prev = { p.X, p.Y, p.Z }
                        end
                        now[id] = prev
                    end
                end
            end
            for id in pairs(seen) do
                if not now[id] and not seen[id].skip then emit("E\t" .. id) end
            end
            seen = now
            flush()
        end)
end)
