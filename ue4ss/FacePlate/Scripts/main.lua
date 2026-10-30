-- FacePlate: a hex plate locked to each other player's head bone.
-- The webcam picture is a jpeg written by the face window:
--   D:\Tools\tos-faceplates\<PlayerId>.jpg
-- This is a mesh in the world. It is not a window on top of the game.
-- The plate's pose is written to plate.txt in this mod folder
-- so the face window can move with the same hex.
-- The in-game hex is hidden. The outline window is the face you see.

local UEHelpers = require("UEHelpers")

local modDir = debug.getinfo(1, "S").source:match("^@(.*)[/\\]Scripts[/\\]")
local logf = io.open((modDir or ".") .. "\\faceplate.log", "w")
local function log(s)
    if logf then logf:write(os.date("%H:%M:%S "), s, "\n") logf:flush() end
end

local function valid(o)
    return o ~= nil and o:IsValid()
end

local function call(label, fn)
    local ok, err = pcall(fn)
    if not ok then log(label .. " failed: " .. tostring(err)) end
    return ok
end

local sockets = { "head", "Head", "head_01", "HeadSocket", "neck_01", "Neck", "spine_03" }
local plates = {}
local sizes = {}

local function playerId(pawn)
    if not valid(pawn) or not valid(pawn.PlayerState) then return nil end
    local id = pawn.PlayerState.PlayerId
    if type(id) ~= "number" or id <= 0 then return nil end
    return math.floor(id)
end

local function headSocket(mesh)
    for _, n in ipairs(sockets) do
        local ok, yes = pcall(function() return mesh:DoesSocketExist(FName(n)) end)
        if ok and yes then return n end
    end
    return nil
end

local function vec(x, y, z)
    return { X = x, Y = y, Z = z }
end

local function identityTransform()
    return {
        Rotation = { X = 0, Y = 0, Z = 0, W = 1 },
        Translation = vec(0, 0, 0),
        Scale3D = vec(1, 1, 1),
    }
end

local function findClass(path)
    local ok, cls = pcall(StaticFindObject, path)
    if ok and valid(cls) then return cls end
    return nil
end

local function hexArrays()
    -- Head bone +X is up. +Z is her right. -Y was the back of the head, so the face is +Y.
    local y = 18
    local s = 1.35
    local function p(x, z)
        return vec(x * s, y, z * s)
    end
    local v = {
        p(11, -8), p(11, 8), p(0, 14),
        p(-11, 8), p(-11, -8), p(0, -14),
    }
    local n, uv, col = {}, {}, {}
    for i = 1, 6 do
        n[i] = vec(0, 1, 0)
        col[i] = { R = 1, G = 1, B = 1, A = 1 }
    end
    -- Head bone +Z is the character's right, which is the viewer's left. The
    -- unmirrored camera frame has u = 0 on that side. v = 0 is the top (+X).
    uv[1] = { X = 0.786, Y = 0 }
    uv[2] = { X = 0.214, Y = 0 }
    uv[3] = { X = 0, Y = 0.5 }
    uv[4] = { X = 0.214, Y = 1 }
    uv[5] = { X = 0.786, Y = 1 }
    uv[6] = { X = 1, Y = 0.5 }
    local tri = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 2, 1, 0, 3, 2, 0, 4, 3, 0, 5, 4 }
    return v, tri, n, uv, col
end

local function selfArrays()
    -- Card in front of the eyes, a little below center, facing the player.
    -- Head bone +X is up, +Z is the character's right, +Y is out of the face.
    local y = 40
    local function p(x, z)
        return vec(x - 8, y, z)
    end
    local v = {
        p(9, -11), p(9, 11), p(0, 16),
        p(-9, 11), p(-9, -11), p(0, -16),
    }
    local n, uv, col = {}, {}, {}
    for i = 1, 6 do
        n[i] = vec(0, -1, 0)
        col[i] = { R = 1, G = 1, B = 1, A = 1 }
    end
    uv[1] = { X = 0.22, Y = 0 }
    uv[2] = { X = 0.78, Y = 0 }
    uv[3] = { X = 1, Y = 0.5 }
    uv[4] = { X = 0.78, Y = 1 }
    uv[5] = { X = 0.22, Y = 1 }
    uv[6] = { X = 0, Y = 0.5 }
    local tri = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 2, 1, 0, 3, 2, 0, 4, 3, 0, 5, 4 }
    return v, tri, n, uv, col
end

-- The picture objects are owned by the game instance, not the character.
-- Rooting an object owned by the character kept the whole map alive and crashed on the way out.
-- Leaving them unrooted let the game delete the camera a second after it opened.
local mediaSerial = 0
local mediaSources = {}
local mediaTextures = {}

local function mediaOuter()
    local outer = nil
    pcall(function()
        local gi = UEHelpers.GetGameInstance()
        if valid(gi) then outer = gi end
    end)
    if valid(outer) then return outer end
    pcall(function()
        local eng = UEHelpers.GetEngine()
        if valid(eng) then outer = eng end
    end)
    return outer
end

local function rootOutsideWorld(obj)
    pcall(function() obj:SetFlags(0x80) end)
end

local function materialRank(full)
    local n = string.lower(full or "")
    if string.find(n, "eye", 1, true) or string.find(n, "teeth", 1, true) or string.find(n, "hair", 1, true) or string.find(n, "lash", 1, true) or string.find(n, "cornea", 1, true) then return 0 end
    if string.find(n, "cloth", 1, true) or string.find(n, "outfit", 1, true) or string.find(n, "shirt", 1, true) or string.find(n, "body", 1, true) or string.find(n, "torso", 1, true) then return 3 end
    if string.find(n, "skin", 1, true) or string.find(n, "head", 1, true) or string.find(n, "face", 1, true) then return 2 end
    return 1
end

local function colorRank(name)
    local n = string.lower(name or "")
    if string.find(n, "normal", 1, true) or string.find(n, "orm", 1, true) or string.find(n, "mask", 1, true) or string.find(n, "rough", 1, true) or string.find(n, "metal", 1, true) then return 0 end
    if string.find(n, "basecolor", 1, true) or string.find(n, "albedo", 1, true) or string.find(n, "diffuse", 1, true) then return 3 end
    if string.find(n, "color", 1, true) or string.find(n, "skin", 1, true) or string.find(n, "body", 1, true) or string.find(n, "cloth", 1, true) then return 2 end
    return 1
end

local function textureNames(mat)
    local names = {}
    if not valid(mat) then return names end
    local arr = nil
    local ok, err = pcall(function() arr = mat.TextureParameterValues end)
    if not ok then
        log("tex read " .. tostring(err))
        return names
    end
    if arr == nil or not arr.GetArrayNum then
        log("tex arr nil")
        return names
    end
    local count = 0
    pcall(function() count = arr:GetArrayNum() or 0 end)
    log("tex count " .. tostring(count))
    if count <= 0 or count > 24 then return names end
    for i = 1, count do
        local oneOk, oneErr = pcall(function()
            local elem = arr[i]
            if elem and elem.get then elem = elem:get() end
            local info = elem and elem.ParameterInfo or nil
            if info and info.get then info = info:get() end
            local nm = info and info.Name or nil
            local text = nil
            if nm and nm.ToString then text = nm:ToString() end
            if text and text ~= "" then
                names[#names + 1] = text
                log("tex " .. text)
            end
        end)
        if not oneOk then log("tex idx " .. i .. " " .. tostring(oneErr)) end
    end
    return names
end

local function attachMedia(plate, pawn)
    local playerClass = findClass("/Script/MediaAssets.MediaPlayer")
    local texClass = findClass("/Script/MediaAssets.MediaTexture")
    if not playerClass or not texClass then
        log("media classes missing")
        return nil
    end
    local outer = mediaOuter()
    if not valid(outer) then
        log("no media outer")
        return nil
    end
    mediaSerial = mediaSerial + 1
    local tag = tostring(mediaSerial)
    -- 0x83 is the flag set that actually kept the camera alive. The outer is the game
    -- instance, so this root does not pin the map the way the character-owned one did.
    local player = StaticConstructObject(playerClass, outer, FName("TosFaceP" .. tag), 0x83)
    local tex = StaticConstructObject(texClass, outer, FName("TosFaceT" .. tag), 0x83)
    if not valid(player) or not valid(tex) then
        log("could not construct media")
        return nil
    end
    rootOutsideWorld(player)
    rootOutsideWorld(tex)
    -- The widget material samples a normal texture. A media texture stays invisible
    -- until it is told to output one, and the plate stays the material's default white.
    local styleOk, styleErr = pcall(function() tex.NewStyleOutput = true end)
    log("style " .. tostring(styleOk) .. " " .. tostring(styleErr))
    pcall(function()
        tex.ClearColor.R = 1
        tex.ClearColor.G = 0
        tex.ClearColor.B = 0
        tex.ClearColor.A = 1
    end)
    local updateOk, updateErr = pcall(function() tex:UpdateResource() end)
    log("texture resource " .. tostring(updateOk) .. " " .. tostring(updateErr))
    call("set player", function() tex:SetMediaPlayer(player) end)
    pcall(function() player.PlayOnOpen = true end)
    pcall(function() player:SetLooping(true) end)
    local img = findClass("/Script/ImgMedia.ImgMediaSource")
    if valid(img) then
        local source = StaticConstructObject(img, outer, FName("TosFaceS" .. tag), 0x83)
        if valid(source) then
            rootOutsideWorld(source)
            local addr = nil
            pcall(function() addr = player:GetAddress() end)
            if addr then
                mediaSources[addr] = source
                mediaTextures[addr] = tex
            end
            log("img source made")
        else
            log("img source failed")
        end
    else
        log("img source false")
    end
    local outerName = ""
    pcall(function() outerName = outer:GetFullName() end)
    log("media rooted " .. tag .. " " .. tostring(outerName))

    -- The head skin shader has no texture name we can set, so it kept drawing the avatar face.
    -- Widget3DPassThrough is the engine material a 3D widget uses. Its texture parameter is SlateUI.
    local mat = nil
    local paths = {
        "/Engine/EngineMaterials/Widget3DPassThrough_Opaque.Widget3DPassThrough_Opaque",
        "/Engine/EngineMaterials/Widget3DPassThrough_Opaque",
    }
    for _, path in ipairs(paths) do
        mat = findClass(path)
        if valid(mat) then break end
    end
    if not valid(mat) then
        pcall(LoadAsset, "/Engine/EngineMaterials/Widget3DPassThrough_Opaque")
        for _, path in ipairs(paths) do
            mat = findClass(path)
            if valid(mat) then break end
        end
    end
    if not valid(mat) then
        log("plate material missing")
        return player
    end
    local full = ""
    pcall(function() full = mat:GetFullName() end)
    log("plate mat " .. tostring(full))
    local made = nil
    local madeOk, got = pcall(function()
        return plate:CreateDynamicMaterialInstance(0, mat, FName("tosface" .. tag))
    end)
    if madeOk and valid(got) then made = got end
    if not valid(made) then
        log("plate instance failed")
        return player
    end
    local out = {}
    local slateOk, slateRet = pcall(function()
        return made:GetTextureParameterValue(FName("SlateUI"), out)
    end)
    log("slate " .. tostring(slateOk) .. " " .. tostring(slateRet))
    local setOk, setErr = pcall(function()
        made:SetTextureParameterValue(FName("SlateUI"), tex)
    end)
    log("slate set " .. tostring(setOk) .. " " .. tostring(setErr))
    local overrides = -1
    pcall(function() overrides = made.TextureParameterValues:GetArrayNum() end)
    log("overrides " .. tostring(overrides))
    pcall(function() plate:SetMaterial(0, made) end)
    log("picture slate")
    return player
end

local plainSerial = 0
local function paintPlain(plate)
    local mat = nil
    local paths = {
        "/Engine/EngineMaterials/Widget3DPassThrough_Opaque.Widget3DPassThrough_Opaque",
        "/Engine/EngineMaterials/Widget3DPassThrough_Opaque",
    }
    for _, path in ipairs(paths) do
        mat = findClass(path)
        if valid(mat) then break end
    end
    if not valid(mat) then
        pcall(LoadAsset, "/Engine/EngineMaterials/Widget3DPassThrough_Opaque")
        for _, path in ipairs(paths) do
            mat = findClass(path)
            if valid(mat) then break end
        end
    end
    if not valid(mat) then
        log("plate material missing")
        return
    end
    plainSerial = plainSerial + 1
    local made = nil
    local madeOk, got = pcall(function()
        return plate:CreateDynamicMaterialInstance(0, mat, FName("tosplain" .. tostring(plainSerial)))
    end)
    if madeOk and valid(got) then made = got end
    if not valid(made) then
        log("plain plate failed")
        return
    end
    pcall(function() plate:SetMaterial(0, made) end)
    log("picture off")
    return made
end

local mirror = nil

local function mirrorArrays()
    local s = 7
    local function p(y, z)
        return vec(0, y, z)
    end
    local v = {
        p(-s * 0.7, s), p(s * 0.7, s), p(s * 1.2, 0),
        p(s * 0.7, -s), p(-s * 0.7, -s), p(-s * 1.2, 0),
    }
    local n, uv, col = {}, {}, {}
    for i = 1, 6 do
        n[i] = vec(-1, 0, 0)
        col[i] = { R = 1, G = 1, B = 1, A = 1 }
    end
    uv[1] = { X = 0.22, Y = 0 }
    uv[2] = { X = 0.78, Y = 0 }
    uv[3] = { X = 1, Y = 0.5 }
    uv[4] = { X = 0.78, Y = 1 }
    uv[5] = { X = 0.22, Y = 1 }
    uv[6] = { X = 0, Y = 0.5 }
    local tri = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 2, 1, 0, 3, 2, 0, 4, 3, 0, 5, 4 }
    return v, tri, n, uv, col
end

local function camBasis(rot)
    local yaw = (rot.Yaw or 0) * math.pi / 180
    local pitch = (rot.Pitch or 0) * math.pi / 180
    local cp, sp = math.cos(pitch), math.sin(pitch)
    local cy, sy = math.cos(yaw), math.sin(yaw)
    local fwd = { X = cp * cy, Y = cp * sy, Z = sp }
    local right = { X = -sy, Y = cy, Z = 0 }
    local up = {
        X = fwd.Y * right.Z - fwd.Z * right.Y,
        Y = fwd.Z * right.X - fwd.X * right.Z,
        Z = fwd.X * right.Y - fwd.Y * right.X,
    }
    return fwd, right, up
end

local function placeMirror(row, pc)
    if not valid(row.comp) or not valid(pc) then return end
    local mgr = pc.PlayerCameraManager
    if not valid(mgr) then return end
    local loc, rot
    local ok = pcall(function()
        loc = mgr:GetCameraLocation()
        rot = mgr:GetCameraRotation()
    end)
    if not ok or not loc or not rot then
        if not row.poseLogged then
            row.poseLogged = true
            log("mirror camera failed")
        end
        return
    end
    local fwd, right, up = camBasis(rot)
    local pos = {
        X = loc.X + fwd.X * 55 + right.X * -28 + up.X * -12,
        Y = loc.Y + fwd.Y * 55 + right.Y * -28 + up.Y * -12,
        Z = loc.Z + fwd.Z * 55 + right.Z * -28 + up.Z * -12,
    }
    pcall(function()
        row.comp:K2_SetWorldLocation(pos, false, {}, false)
        row.comp:K2_SetWorldRotation(rot, false, {}, false)
    end)
end

local function makeMirror(pawn, id)
    local cls = findClass("/Script/ProceduralMeshComponent.ProceduralMeshComponent")
    if not cls then log("ProceduralMeshComponent class missing") return nil end
    local plate = nil
    local addedOk, added = pcall(function()
        return pawn:AddComponentByClass(cls, true, identityTransform(), false)
    end)
    if addedOk and valid(added) then
        plate = added
    else
        plate = StaticConstructObject(cls, pawn)
        if not valid(plate) then return nil end
    end
    call("mobility", function() plate:SetMobility(2) end)
    call("collision", function() plate:SetCollisionEnabled(0) end)
    call("shadow", function() plate:SetCastShadow(false) end)
    call("show", function() plate:SetHiddenInGame(false, true) end)
    local v, tri, nrm, uv, col = mirrorArrays()
    local built = call("mirror-mesh", function()
        plate:CreateMeshSection_LinearColor(0, v, tri, nrm, uv, {}, {}, {}, col, {}, false, false)
    end)
    if not built then
        call("mirror-mesh-basic", function()
            plate:CreateMeshSection(0, v, tri, nrm, uv, {}, {}, false)
        end)
    end
    local player = attachMedia(plate, pawn)
    log("mirror on " .. id)
    return { comp = plate, player = player, id = id }
end

-- Diamond <-> hex on the face plane. Same rules as M2A overlay_geometry and
-- shape_controller: shared vertex order so the morph is a plain lerp, the diamond
-- is a degenerate hex (top two verts coincide, bottom two coincide), diamond
-- width = height / 1.45, 400 ms ease-out cubic-bezier(0, 0, 0.2, 1).
-- The picture does not change: each vertex takes its UV from its place on the
-- plane, so the shape only crops it.
local FACE_S = 1.35
local FACE_X = { 11, 11, 0, -11, -11, 0 }
-- Kaalo 2026-10-28: plate up a little; the diamond keeps its size; the hex grows
-- DOWN to the stomach (top flats stay at 11, bottom flats go to -28, the side
-- points to the middle) and keeps its SHAPE: it is the old hex scaled up evenly,
-- so it gets wider as it gets taller and the picture is not stretched.
-- Plate units, scaled by FACE_S and the head-bone scale.
-- Kaalo 2026-10-29: a little bigger still, it did not reach the stomach. HEX_K scales
-- the whole hex evenly with the top flats fixed at 11, so it grows down and out.
-- Kaalo 2026-10-29: every hex in the game is the wide Left Arrow hex: the camera's own
-- 16:9 across the whole hex (no crop, nothing stretched), top and bottom corners 75%
-- of the half width so the left and right tips are blunt. Same numbers in
-- BigHud.cs, SlideHud.cs and ShapeHud.cs.
local HEX_TALL = 44
local HEX_HALFW = HEX_TALL * (16 / 9) / 2
local HEX_X = { 11, 11, 11 - HEX_TALL / 2, 11 - HEX_TALL, 11 - HEX_TALL, 11 - HEX_TALL / 2 }
local HEX_Z = { -0.75 * HEX_HALFW, 0.75 * HEX_HALFW, HEX_HALFW, 0.75 * HEX_HALFW, -0.75 * HEX_HALFW, -HEX_HALFW }
local PLATE_UP = 3
local shapeBlend = 0
-- Default (confirmed by Kaalo 2026-10-28): only the first winding, which faces
-- OUT, unmirrored for a viewer in front of the character. The second winding is
-- the one that faces the back of the head; the material only draws a picture on
-- the engine-front side, so showing both gave a black plate from the front.
-- facing.txt beside this script overrides that live, no restart. Words in it:
-- "mu" mirrors left/right, "mv" flips up/down, "sa" = first winding only,
-- "sb" = second only, "nd" = normals point -Y. No file = "sa".
local flipU, flipV = false, false
local useA, useB, normalDown = true, false, false
-- Kaalo 2026-10-29: a little away from the face again (was 14, 18 before that).
local PLATE_OUT_DEFAULT = 18
local plateOut = PLATE_OUT_DEFAULT
-- Picture rate on the head plate: 30 per second, 720p frames. "fps15" in facing.txt
-- slows it live if the game stutters.
local imageEvery = 1 / 30
local facingAt = 0
local facingText = nil

local function readFacing()
    local now = os.clock()
    if now < facingAt then return false end
    facingAt = now + 1
    local f = io.open((modDir or ".") .. "\\facing.txt", "r")
    local text = "sa"
    if f then
        text = (f:read("*a") or ""):lower()
        f:close()
    end
    if text == facingText then return false end
    facingText = text
    flipU = string.find(text, "mu", 1, true) ~= nil
    flipV = string.find(text, "mv", 1, true) ~= nil
    local sa = string.find(text, "sa", 1, true) ~= nil
    local sb = string.find(text, "sb", 1, true) ~= nil
    useA = sa or not sb
    useB = sb
    normalDown = string.find(text, "nd", 1, true) ~= nil
    -- "out14" = how far the plate floats in front of the face, head-bone units.
    local outText = text:match("out(%d+%.?%d*)")
    plateOut = outText and tonumber(outText) or PLATE_OUT_DEFAULT
    log("plate out " .. tostring(plateOut))
    local fpsText = text:match("fps(%d+)")
    local fps = fpsText and tonumber(fpsText) or 30
    if fps < 1 then fps = 1 elseif fps > 60 then fps = 60 end
    imageEvery = 1 / fps
    log("plate picture " .. tostring(fps) .. " per second")
    log("facing mu=" .. tostring(flipU) .. " mv=" .. tostring(flipV) .. " A=" .. tostring(useA) .. " B=" .. tostring(useB) .. " nd=" .. tostring(normalDown))
    return true
end

-- Face tracking, M2A diamond_tracking: the diamond roams over the footage, then the
-- footage pans. TosFace measures the other player's face in their camera frames and
-- writes track.txt ("dx pan", feed pixels of a 1280 wide feed, already smoothed).
-- Horizontal only, no zoom. The hex always shows the whole frame, so it ignores this.
local trackDx, trackPan = 0, 0
local trackTargetDx, trackTargetPan = 0, 0
local trackAt = 0

local function readTrack()
    local now = os.clock()
    if now < trackAt then return end
    trackAt = now + 0.05
    local f = io.open((modDir or ".") .. "\\track.txt", "r")
    if not f then return end
    local text = f:read("*a") or ""
    f:close()
    local a, b = text:match("^%s*(%-?[%d%.]+)%s+(%-?[%d%.]+)")
    if a and b then
        trackTargetDx = tonumber(a) or 0
        trackTargetPan = tonumber(b) or 0
    end
end

-- Where the diamond window sits on the plate (plate units, +Z = viewer left) and how
-- far the footage is panned (fraction of the frame width), kept inside the frame.
local function trackedDiamond()
    local dxf = trackDx / 1280
    local panf = trackPan / 1280
    local hd = (11 / 1.45) / 28
    local uc = 0.5 + dxf - panf
    if uc < hd then panf = 0.5 + dxf - hd elseif uc > 1 - hd then panf = 0.5 + dxf - (1 - hd) end
    return -dxf * 28, panf
end

local function faceArrays(blend)
    local dz = 11 / 1.45
    local diamondZ = { 0, 0, dz, 0, 0, -dz }
    local shiftZ, panf = trackedDiamond()
    local v, uv, n, col = {}, {}, {}, {}
    for i = 1, 6 do
        local x = FACE_X[i] + (HEX_X[i] - FACE_X[i]) * blend
        local zd = diamondZ[i] + shiftZ
        local z = zd + (HEX_Z[i] - zd) * blend
        v[i] = vec((x + PLATE_UP) * FACE_S, plateOut, z * FACE_S)
        -- Head bone +Z is the character's right = the viewer's left = u 0.
        -- The diamond shows the same crop of the frame as before. The hex shows the
        -- whole frame across its taller shape; the UVs blend with the morph.
        local ud = (14 - zd) / 28 - panf
        local wd = (11 - FACE_X[i]) / 22
        local uh = (HEX_HALFW - HEX_Z[i]) / (2 * HEX_HALFW)
        local wh = (11 - HEX_X[i]) / HEX_TALL
        local u = ud + (uh - ud) * blend
        local w = wd + (wh - wd) * blend
        if flipU then u = 1 - u end
        if flipV then w = 1 - w end
        uv[i] = { X = u, Y = w }
        n[i] = vec(0, normalDown and -1 or 1, 0)
        col[i] = { R = 1, G = 1, B = 1, A = 1 }
    end
    local tri = {}
    local setA = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5 }
    local setB = { 0, 2, 1, 0, 3, 2, 0, 4, 3, 0, 5, 4 }
    if useA then for _, k in ipairs(setA) do tri[#tri + 1] = k end end
    if useB then for _, k in ipairs(setB) do tri[#tri + 1] = k end end
    return v, tri, n, uv, col
end

local function hexArrays()
    return faceArrays(shapeBlend)
end

local function rebuildPlate(row, blend)
    local v, tri, nrm, uv, col = faceArrays(blend)
    row.localVerts = v
    local ok = pcall(function()
        row.comp:CreateMeshSection_LinearColor(0, v, tri, nrm, uv, {}, {}, {}, col, {}, false, false)
    end)
    if not ok then
        pcall(function() row.comp:CreateMeshSection(0, v, tri, nrm, uv, {}, {}, false) end)
    end
end

local shapeIsHex, shapeAnim = false, false
local animFrom, animTo, animStart, pendingShape = 0, 0, 0, -1
local keyWasDown, lockFired, hexLocked = false, false, false
local keyDownAt, hexUntil = 0, 0
local keyPolled = false
local keyFailed = false
local drawnBlend = 0

local function bez(u, a, b, c, d)
    local w = 1 - u
    return w * w * w * a + 3 * w * w * u * b + 3 * w * u * u * c + u * u * u * d
end

local function bezD(u, a, b, c, d)
    local w = 1 - u
    return 3 * w * w * (b - a) + 6 * w * u * (c - b) + 3 * u * u * (d - c)
end

local function easeOut(x)
    local u = x
    for _ = 1, 8 do
        local bx = bez(u, 0, 0, 0.2, 1)
        local dx = bezD(u, 0, 0, 0.2, 1)
        if math.abs(dx) < 1e-5 then break end
        u = u - (bx - x) / dx
        if u < 0 then u = 0 end
        if u > 1 then u = 1 end
    end
    return bez(u, 0, 0, 1, 1)
end

local function requestShape(hex)
    if shapeAnim then
        pendingShape = hex and 1 or 0
        return
    end
    if shapeIsHex == hex then return end
    animFrom = shapeIsHex and 1 or 0
    animTo = hex and 1 or 0
    animStart = os.clock()
    shapeAnim = true
end

local function currentBlend()
    if not shapeAnim then return shapeIsHex and 1 or 0 end
    local t = (os.clock() - animStart) / 0.4
    if t >= 1 then
        shapeIsHex = animTo >= 0.5
        shapeAnim = false
        if pendingShape >= 0 then
            local nextHex = pendingShape == 1
            pendingShape = -1
            requestShape(nextHex)
        end
        return shapeIsHex and 1 or 0
    end
    return animFrom + (animTo - animFrom) * easeOut(t)
end

-- Right Arrow. Tap: hex for 20 s, then diamond. A tap during those 20 s starts
-- them over. Hold 0.5 s: hex locks. Tap while locked: diamond.
local function arrowDown()
    local pc = UEHelpers.GetPlayerController()
    if not valid(pc) then return nil end
    local ok, res = pcall(function() return pc:IsInputKeyDown({ KeyName = FName("Right") }) end)
    if not ok then
        if not keyFailed then
            keyFailed = true
            log("arrow poll failed " .. tostring(res))
        end
        return nil
    end
    keyPolled = true
    return res == true
end

-- Each player controls only their own shape. The key sets MY shape: it is written
-- to shape_self.txt, TosFace publishes it to LiveKit, and the other player's game
-- reads it from their shape_remote.txt. The plate I look at follows the OTHER
-- player's shape (shape_remote.txt), never my own key.
local ownHex = nil
local remoteHex = false
local remoteAt = 0

local function setOwn(hex)
    if ownHex == hex then return end
    ownHex = hex
    local f = io.open((modDir or ".") .. "\\shape_self.txt", "w")
    if f then
        f:write(hex and "hex" or "diamond")
        f:close()
    end
end

local function readRemoteShape()
    local now = os.clock()
    if now < remoteAt then return end
    remoteAt = now + 0.2
    local f = io.open((modDir or ".") .. "\\shape_remote.txt", "r")
    if not f then return end
    local text = (f:read("*a") or ""):lower()
    f:close()
    local hex = string.find(text, "hex", 1, true) ~= nil
    if hex ~= remoteHex then
        remoteHex = hex
        requestShape(hex)
    end
end

local function tapArrow()
    if hexLocked then
        hexLocked = false
        hexUntil = 0
        setOwn(false)
    else
        setOwn(true)
        hexUntil = os.clock() + 20
    end
end

-- Up Arrow: both players' hex cameras slide in from the right screen edge for a
-- few seconds. The press is written to slide_self.txt as a new number; TosFace
-- sends it to the other player and both TosFace windows draw the slide.
local upWasDown = false
local slideCount = 0

local function keyDown(name)
    local pc = UEHelpers.GetPlayerController()
    if not valid(pc) then return nil end
    local ok, res = pcall(function() return pc:IsInputKeyDown({ KeyName = FName(name) }) end)
    if not ok then return nil end
    return res == true
end

-- Left Arrow: my own hex camera, big in the middle of my screen (big_self.txt).
-- Tap: shows it for a few seconds. Hold 0.5 s: it stays up (locked). Tap while it is
-- up: it goes away. Stays on my screen only; TosFace draws it.
local leftWasDown = false
local leftDownAt = 0
local leftLocked = false
local bigCount = 0

local function pressFile(name, count, kind)
    local f = io.open((modDir or ".") .. "\\" .. name, "w")
    if f then
        f:write((kind and (kind .. "-") or "") .. tostring(os.time()) .. "-" .. tostring(count))
        f:close()
    end
end

local function tickSlide()
    local down = keyDown("Up")
    if down ~= nil then
        if down and not upWasDown then
            slideCount = slideCount + 1
            pressFile("slide_self.txt", slideCount)
        end
        upWasDown = down
    end
    local left = keyDown("Left")
    if left ~= nil then
        local now = os.clock()
        if left and not leftWasDown then
            leftDownAt = now
            leftLocked = false
        end
        if left and not leftLocked and leftDownAt > 0 and now - leftDownAt >= 0.5 then
            leftLocked = true
            bigCount = bigCount + 1
            pressFile("big_self.txt", bigCount, "lock")
        end
        if not left and leftWasDown and not leftLocked then
            bigCount = bigCount + 1
            pressFile("big_self.txt", bigCount, "tap")
        end
        if not left then leftDownAt = 0 end
        leftWasDown = left
    end
end

local function tickShape()
    local now = os.clock()
    tickSlide()
    local down = arrowDown()
    if down ~= nil then
        if down and not keyWasDown then
            keyDownAt = now
            lockFired = false
        end
        if down and not lockFired and keyDownAt > 0 and now - keyDownAt >= 0.5 then
            lockFired = true
            hexLocked = true
            hexUntil = 0
            setOwn(true)
        end
        if not down and keyWasDown and not lockFired then tapArrow() end
        if not down then keyDownAt = 0 end
        keyWasDown = down
    end
    if hexUntil > 0 and now >= hexUntil and not hexLocked then
        hexUntil = 0
        setOwn(false)
    end
    if ownHex == nil then setOwn(false) end
    readRemoteShape()
    local blend = currentBlend()
    if readFacing() then drawnBlend = -1 end
    readTrack()
    local trackMoved = false
    local ndx = trackDx + (trackTargetDx - trackDx) * 0.3
    local npan = trackPan + (trackTargetPan - trackPan) * 0.3
    if math.abs(ndx - trackDx) > 0.4 or math.abs(npan - trackPan) > 0.4 then
        trackDx, trackPan = ndx, npan
        trackMoved = blend < 0.999
    end
    if math.abs(blend - drawnBlend) > 0.002 or trackMoved then
        drawnBlend = blend
        shapeBlend = blend
        for _, row in pairs(plates) do
            if type(row) == "table" and not row.self and valid(row.comp) then rebuildPlate(row, blend) end
        end
    end
end

-- If this game build will not report the key, fall back to a plain tap bind.
local function bindArrowFallback()
    if type(RegisterKeyBind) ~= "function" or not Key then return end
    local ok, err = pcall(function()
        RegisterKeyBind(Key.RIGHT, function() ExecuteInGameThread(function() pcall(tapArrow) end) end)
    end)
    log("arrow keybind " .. tostring(ok) .. " " .. tostring(err))
end

local function makePlate(pawn, mesh, socket, id, arrays)
    local cls = findClass("/Script/ProceduralMeshComponent.ProceduralMeshComponent")
    if not cls then log("ProceduralMeshComponent class missing") return nil end
    local plate = nil
    local addedOk, added = pcall(function()
        return pawn:AddComponentByClass(cls, true, identityTransform(), false)
    end)
    if addedOk and valid(added) then
        plate = added
    else
        log("AddComponentByClass failed: " .. tostring(added))
        plate = StaticConstructObject(cls, pawn)
        if not valid(plate) then log("could not construct the plate") return nil end
        call("finish", function() pawn:FinishAddComponent(plate, true, identityTransform()) end)
    end
    call("mobility", function() plate:SetMobility(2) end)
    call("attach", function()
        plate:K2_AttachToComponent(mesh, FName(socket), 2, 2, 2, true)
    end)
    call("collision", function() plate:SetCollisionEnabled(0) end)
    call("shadow", function() plate:SetCastShadow(false) end)
    call("hide", function() plate:SetHiddenInGame(true, true) end)
    local v, tri, nrm, uv, col = (arrays or hexArrays)()
    local built = call("mesh", function()
        plate:CreateMeshSection_LinearColor(0, v, tri, nrm, uv, {}, {}, {}, col, {}, false, false)
    end)
    if not built then
        call("mesh-basic", function()
            plate:CreateMeshSection(0, v, tri, nrm, uv, {}, {}, false)
        end)
    end
    local made = paintPlain(plate)
    log("plate on " .. id .. " socket " .. socket)
    return { comp = plate, mat = made, player = nil, id = id, localVerts = v }
end

local function fileSize(path)
    local f = io.open(path, "rb")
    if not f then return 0 end
    local n = f:seek("end")
    f:close()
    return n or 0
end

local function copyFile(srcPath, dstPath)
    local src = io.open(srcPath, "rb")
    if not src then return false end
    local data = src:read("*a")
    src:close()
    if not data or #data < 400 then return false end
    local dst = io.open(dstPath, "wb")
    if not dst then return false end
    dst:write(data)
    dst:close()
    return true
end

local function readBin(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local data = f:read("*a")
    f:close()
    if not data or #data < 400 then return nil end
    return data
end

local function writeBin(path, data)
    local f = io.open(path, "wb")
    if not f then return false end
    f:write(data)
    f:close()
    return true
end

local function deviceListUrl(arr)
    if type(arr) ~= "userdata" or not arr.GetArrayNum then return nil end
    local n = 0
    pcall(function() n = arr:GetArrayNum() or 0 end)
    log("cameras " .. tostring(n))
    if n <= 0 or n > 8 then return nil end
    local chosen = nil
    for i = 1, n do
        local oneOk, oneErr = pcall(function()
            local elem = arr[i]
            if elem and elem.get then elem = elem:get() end
            local name, url = "", ""
            pcall(function()
                local dn = elem.DisplayName
                if type(dn) == "string" then name = dn
                elseif dn and dn.ToString then name = dn:ToString() end
            end)
            pcall(function() url = tostring(elem.Url or "") end)
            log("camera " .. i .. " " .. tostring(name) .. " " .. string.sub(url, 1, 9))
            local low = string.lower(name)
            local skip = string.find(low, "virtual", 1, true) or string.find(low, "obs", 1, true)
            if url ~= "" and not skip then chosen = chosen or url end
            if url ~= "" then chosen = chosen or url end
        end)
        if not oneOk then log("camera " .. i .. " err " .. tostring(oneErr)) end
    end
    return chosen
end

local function cameraUrl(player)
    local lib = findClass("/Script/MediaAssets.Default__MediaBlueprintFunctionLibrary")
    if not valid(lib) then
        log("camera lib missing")
    else
        -- Webcam filter is 8. The device list comes back as the return value.
        for _, args in ipairs({ { 8 }, { 15 }, {} }) do
            local packed
            if args[1] then
                packed = { pcall(function() return lib:EnumerateVideoCaptureDevices(args[1]) end) }
            else
                packed = { pcall(function() return lib:EnumerateVideoCaptureDevices() end) }
            end
            if not packed[1] then
                log("enum " .. tostring(packed[2]))
            else
                log("enum returns " .. tostring(#packed - 1))
                for i = 2, #packed do
                    local url = deviceListUrl(packed[i])
                    if url then return url end
                end
            end
        end
    end
    local f = io.open("D:/Tools/tos-faceplates/camera.url", "r")
    if f then
        local url = f:read("*l")
        f:close()
        if url and string.sub(url, 1, 9) == "vidcap://" then
            log("camera file")
            return url
        end
    end
    return nil
end

local function refreshMedia(row)
    if not row.player or not valid(row.player) then
        if not row.missMedia then
            row.missMedia = true
            log("no picture player for " .. tostring(row.id))
        end
        return
    end
    local now = os.clock()
    if row.probeAt and now >= row.probeAt and not row.probed then
        row.probed = true
        local playing, ready = "err", "err"
        local playOk, playRet = pcall(function() return row.player:IsPlaying() end)
        local readyOk, readyRet = pcall(function() return row.player:IsReady() end)
        if playOk then playing = tostring(playRet) else playing = tostring(playRet) end
        if readyOk then ready = tostring(readyRet) else ready = tostring(readyRet) end
        log("player playing " .. playing .. " ready " .. ready)
    end
    if row.nextOpen and now < row.nextOpen then return end
    row.nextOpen = now + 0.2
    -- The game video player will not start the webcam. Each new photo goes in its own
    -- folder so the image player cannot keep showing an older cached frame.
    local path = "D:/Tools/tos-faceplates/" .. tostring(row.file or row.id) .. ".jpg"
    local data = readBin(path)
    if not data or data == row.lastData then return end
    row.lastData = data
    row.frameNo = (row.frameNo or 0) + 1
    if row.frameNo > 9999 then row.frameNo = 1 end
    local dir = string.format("D:/Tools/tos-faceplates/live/%s/%05d", tostring(row.file or row.id), row.frameNo)
    local win = dir:gsub("/", "\\")
    if os.execute then os.execute('cmd /c mkdir "' .. win .. '"') end
    if row.frameNo > 3 then
        local old = string.format("D:/Tools/tos-faceplates/live/%s/%05d", tostring(row.file or row.id), row.frameNo - 3)
        if os.execute then os.execute('cmd /c rmdir /s /q "' .. old:gsub("/", "\\") .. '"') end
    end
    local seq = dir .. "/frame.0001.jpg"
    if not writeBin(seq, data) then
        if not row.opened then log("live write failed") end
        return
    end
    local source = nil
    pcall(function() source = mediaSources[row.player:GetAddress()] end)
    local openedFlag = nil
    local callOk, callErr
    if valid(source) then
        local setOk, setErr = pcall(function() source:SetSequencePath(seq) end)
        if not row.opened then log("sequence " .. tostring(setOk) .. " " .. tostring(setErr)) end
        callOk, callErr = pcall(function() openedFlag = row.player:OpenSource(source) end)
    else
        callOk, callErr = pcall(function() openedFlag = row.player:OpenFile(path) end)
    end
    if openedFlag then pcall(function() row.player:Play() end) end
    row.swaps = (row.swaps or 0) + 1
    if not row.opened or row.swaps % 20 == 0 then
        row.opened = true
        row.probeAt = row.probeAt or (now + 1)
        log("live " .. tostring(row.swaps) .. " " .. tostring(callOk) .. " " .. tostring(openedFlag) .. " " .. tostring(callErr))
    end
end

-- The picture is drawn by the engine on the plate mesh, so it moves with the
-- head bone on the same frame. TosFace writes the other player's camera as
-- remote.jpg beside this script; each new frame is imported as a texture.
local renderLib = nil
local imageNotes = 0

-- The plate shows its picture on the outer face only; its back is plain black.
-- Kaalo 2026-10-29: make the back invisible, as if the plate is not there. When the
-- camera is behind the plate the whole plate is hidden (the same side test as the old
-- overlay: (camera - face centre) . outward normal <= 0, normal = component +Y).
local function applyVisible(row)
    local want = row.shown and not row.behind
    if want == row.vis then return end
    row.vis = want
    pcall(function() row.comp:SetHiddenInGame(not want, true) end)
end

local function gateBehind(row)
    if row.self or not row.shown or not valid(row.comp) then return end
    local pc = UEHelpers.GetPlayerController()
    if not valid(pc) or not valid(pc.PlayerCameraManager) then return end
    local cl, loc, rot, sc
    local ok = pcall(function()
        cl = pc.PlayerCameraManager:GetCameraLocation()
        loc = row.comp:K2_GetComponentLocation()
        rot = row.comp:K2_GetComponentRotation()
        sc = row.comp:K2_GetComponentScale()
    end)
    if not ok or not cl or not loc or not rot then return end
    local rad = math.pi / 180
    local sp, cp = math.sin((rot.Pitch or 0) * rad), math.cos((rot.Pitch or 0) * rad)
    local sy, cy = math.sin((rot.Yaw or 0) * rad), math.cos((rot.Yaw or 0) * rad)
    local sr, cr = math.sin((rot.Roll or 0) * rad), math.cos((rot.Roll or 0) * rad)
    local nx = sr * sp * cy - cr * sy
    local ny = sr * sp * sy + cr * cy
    local nz = -sr * cp
    local scale = (sc and type(sc.Y) == "number") and sc.Y or 1
    local fx = loc.X + nx * plateOut * scale
    local fy = loc.Y + ny * plateOut * scale
    local fz = loc.Z + nz * plateOut * scale
    local facing = (cl.X - fx) * nx + (cl.Y - fy) * ny + (cl.Z - fz) * nz
    local behind = facing <= 0
    if behind ~= row.behind then
        row.behind = behind
        applyVisible(row)
    end
end

local function refreshImage(row)
    if row.self or not valid(row.comp) or not row.mat or not valid(row.mat) then return end
    local now = os.clock()
    if row.nextImage and now < row.nextImage then return end
    row.nextImage = now + imageEvery
    local path = (modDir or ".") .. "\\remote.jpg"
    local data = readBin(path)
    if data and data ~= row.lastData then
        row.lastData = data
        row.lastFrameAt = now
        if not valid(renderLib) then
            renderLib = findClass("/Script/Engine.Default__KismetRenderingLibrary")
        end
        local tex = nil
        if valid(renderLib) then
            local world = nil
            pcall(function() world = UEHelpers.GetWorld() end)
            local ok, ret = pcall(function() return renderLib:ImportFileAsTexture2D(world, path) end)
            if ok and valid(ret) then
                tex = ret
            elseif imageNotes < 5 then
                imageNotes = imageNotes + 1
                log("import texture failed " .. tostring(ret))
            end
        elseif imageNotes < 5 then
            imageNotes = imageNotes + 1
            log("KismetRenderingLibrary missing")
        end
        if tex then
            local setOk, setErr = pcall(function() row.mat:SetTextureParameterValue(FName("SlateUI"), tex) end)
            if not setOk and imageNotes < 5 then
                imageNotes = imageNotes + 1
                log("set picture failed " .. tostring(setErr))
            end
            if setOk and not row.shown then
                row.shown = true
                applyVisible(row)
                log("picture on " .. tostring(row.id))
            end
        end
    end
    -- No new frame for 3 seconds: the other player left or turned the camera off.
    if row.shown and row.lastFrameAt and now - row.lastFrameAt > 3 then
        row.shown = false
        applyVisible(row)
        log("picture off " .. tostring(row.id))
    end
end

local function characters()
    local list = {}
    for _, cls in ipairs({ "BP_Character_HQ_C", "BP_Character_Survivor_C" }) do
        local found = FindAllOf(cls)
        if found then
            for _, p in ipairs(found) do
                if valid(p) then list[#list + 1] = p end
            end
        end
    end
    return list
end

local sharePath = (modDir or "D:/Tools/tos-faceplates") .. "/plate.txt"
local shareNoted = false
local shareReady = false
local shareHooked = false
local shareSeq = 0
local shareHandle = nil

-- The screen overlay is gone. The pose file only fed it.
local POSE_FILE = false

local function writeShare()
    if not POSE_FILE then return end
    local pc = UEHelpers.GetPlayerController()
    if not valid(pc) or not valid(pc.PlayerCameraManager) then return end
    local mgr = pc.PlayerCameraManager
    local cl, cr
    local okCam = pcall(function()
        cl = mgr:GetCameraLocation()
        cr = mgr:GetCameraRotation()
    end)
    if not okCam or not cl or not cr then return end
    local fov = 90
    local fovOk, fovRet = pcall(function() return mgr:GetFOVAngle() end)
    if fovOk and type(fovRet) == "number" and fovRet > 20 and fovRet < 170 then fov = fovRet end
    local lines = {
        string.format("C %.3f %.3f %.3f %.3f %.3f %.3f %.3f",
            cr.Pitch or 0, cr.Yaw or 0, cr.Roll or 0, cl.X, cl.Y, cl.Z, fov)
    }
    local sample = nil
    local meId = nil
    for _, row in pairs(plates) do
        if type(row) == "table" and valid(row.comp) and row.localVerts then
            local loc, rot
            local okLoc = pcall(function()
                loc = row.comp:K2_GetComponentLocation()
                rot = row.comp:K2_GetComponentRotation()
            end)
            local sx, sy, sz = 1, 1, 1
            local sc
            local okSc = pcall(function() sc = row.comp:K2_GetComponentScale() end)
            if okSc and sc and type(sc.X) == "number" and math.abs(sc.X) > 0.01 then
                sx, sy, sz = sc.X, sc.Y, sc.Z
            end
            if okLoc and loc and rot then
                local parts = {
                    string.format("P %s %.3f %.3f %.3f %.3f %.3f %.3f %.4f %.4f %.4f",
                        tostring(row.id), loc.X, loc.Y, loc.Z,
                        rot.Pitch or 0, rot.Yaw or 0, rot.Roll or 0, sx, sy, sz)
                }
                for i = 1, 6 do
                    local p = row.localVerts[i]
                    parts[#parts + 1] = string.format(" %.3f %.3f %.3f", p.X, p.Y, p.Z)
                end
                lines[#lines + 1] = table.concat(parts)
                if row.self then meId = tostring(row.id) end
                sample = sample or string.format("share plate %s loc %.0f %.0f %.0f", tostring(row.id), loc.X, loc.Y, loc.Z)
            elseif not row.shareErr then
                row.shareErr = true
                log("share pose failed for " .. tostring(row.id))
            end
        end
    end
    if meId then lines[#lines + 1] = "M " .. meId end
    -- Game clock in ms. The overlay uses it to measure how fast the head moves.
    lines[#lines + 1] = string.format("T %.1f", os.clock() * 1000)
    local payload = table.concat(lines, "\n") .. "\n"
    shareSeq = (shareSeq % 1000000) + 1
    local body = "S " .. shareSeq .. "\n" .. payload .. "E " .. shareSeq .. "\n"
    if #body < 2048 then body = body .. string.rep(" ", 2048 - #body) end
    if not shareHandle then
        shareHandle = io.open(sharePath, "w+")
        if shareHandle then pcall(function() shareHandle:setvbuf("no") end) end
    end
    if not shareHandle then
        if not shareNoted then log("share write failed") end
        shareNoted = true
        return
    end
    shareHandle:seek("set", 0)
    shareHandle:write(body)
    shareHandle:flush()
    if not shareNoted then
        shareNoted = true
        log("share on fov " .. string.format("%.1f", fov) .. " " .. tostring(fovOk))
        if sample then log(sample .. string.format(" cam %.0f %.0f %.0f", cl.X, cl.Y, cl.Z)) end
    end
end

do
    local clear = io.open(sharePath, "w")
    if clear then clear:write("NONE\n") clear:close() end
end

-- The freshest pose is after the engine finished the frame: animation has moved
-- the head bone and the camera has updated. PlayerTick runs before both, so the
-- plate trailed the head by about a frame. Use the engine post tick when this
-- UE4SS has it. Keep PlayerTick as the fallback.
local okHook, errHook = false, "none"
local hookKind = "none"
if type(RegisterEngineTickPostCallback) == "function" then
    okHook, errHook = pcall(function()
        RegisterEngineTickPostCallback(function()
            if not shareReady then return end
            pcall(writeShare)
        end)
    end)
    if okHook then hookKind = "engine post tick" end
end
if not okHook then
    okHook, errHook = pcall(function()
        RegisterHook("/Script/Engine.PlayerController:PlayerTick", function()
            if not shareReady then return end
            pcall(writeShare)
        end)
    end)
    if okHook then hookKind = "PlayerTick" end
end
log("share hook kind " .. hookKind)
shareHooked = okHook
log("share hook " .. tostring(okHook) .. " " .. tostring(errHook))

log("FacePlate loaded build 57")

local bootTicks = 0

LoopInGameThreadWithDelay(16, function()
    local okTick, errTick = pcall(function()
        bootTicks = bootTicks + 1
        if bootTicks == 180 then log("tick alive") end
        -- The material bind crashed when it ran during the first seconds of boot.
        if bootTicks < 500 then return end
        if not shareReady then
            shareReady = true
            log("share frame")
        end
        for _, row in pairs(plates) do
            if type(row) == "table" then
                pcall(refreshImage, row)
                pcall(gateBehind, row)
            end
        end
        pcall(tickShape)
        if keyFailed and not plates.arrowBound then
            plates.arrowBound = true
            bindArrowFallback()
        end
        if bootTicks % 12 ~= 0 then
            if not shareHooked then writeShare() end
            return
        end
        local _, me = nil, nil
        local pc = UEHelpers.GetPlayerController()
        if valid(pc) then me = pc.Pawn end
        local myAddr = valid(me) and me:GetAddress() or 0
        if valid(me) then
            local myId = playerId(me)
            local myMesh = me.Mesh
            if myId and valid(myMesh) and plates[myAddr] == nil then
                local socket = headSocket(myMesh)
                plates[myAddr] = false
                if not socket then
                    log("no head socket on self " .. myId)
                else
                    -- Same outward face hex as the other player. The eye card faced back at the camera.
                    local ok, res = pcall(makePlate, me, myMesh, socket, myId, hexArrays)
                    if ok and type(res) == "table" and valid(res.comp) then
                        res.file = "0"
                        res.self = true
                        plates[myAddr] = res
                        log("self plate on " .. myId)
                    else
                        log("self plate failed: " .. tostring(res))
                    end
                end
            end

        end
        local seen = {}
        for _, pawn in ipairs(characters()) do
            local addr = pawn:GetAddress()
            seen[addr] = true
            if addr ~= myAddr then
                local id = playerId(pawn)
                local mesh = pawn.Mesh
                if id and valid(mesh) and plates[addr] == nil then
                    local socket = headSocket(mesh)
                    plates[addr] = false
                    if not socket then
                        log("no head socket on " .. id)
                    else
                        local ok, res = pcall(makePlate, pawn, mesh, socket, id)
                        if ok and type(res) == "table" and valid(res.comp) then
                            plates[addr] = res
                        else
                            log("plate failed for " .. id .. " socket " .. tostring(socket) .. ": " .. tostring(res))
                        end
                    end
                end

            end
        end
        for addr, row in pairs(plates) do
            if type(row) == "table" and valid(row.comp) then
                if seen[addr] then
                    row.miss = 0
                else
                    row.miss = (row.miss or 0) + 1
                    if row.miss >= 25 then
                        pcall(function()
                            if valid(row.player) and row.opened then row.player:Close() end
                        end)
                        pcall(function() row.comp:K2_DestroyComponent(row.comp) end)
                        plates[addr] = nil
                    end
                end
            end
        end
        if not shareHooked then writeShare() end
    end)
    if not okTick and not plates.tickErr then
        plates.tickErr = true
        log("tick failed: " .. tostring(errTick))
    end
end)
