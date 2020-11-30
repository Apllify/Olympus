local ui, uiu, uie = require("ui").quick()
local utils = require("utils")
local threader = require("threader")
local scener = require("scener")
local alert = require("alert")
local fs = require("fs")
local config = require("config")
local sharp = require("sharp")

local scene = {
    name = "Install Manager"
}


local root


function scene.browse()
    return threader.routine(function()
        local type = nil
        local userOS = love.system.getOS()

        if userOS == "Windows" then
            type = "exe"

        elseif userOS == "Linux" then
            type = "exe,bin.x86,bin.x86_64"

        elseif userOS == "OS X" then
            type = "app,exe,bin.osx"
        end

        local path = fs.openDialog(type):result()
        if not path then
            return
        end

        path = require("finder").fixRoot(fs.dirname(path))
        if not path then
            return
        end

        local foundT = threader.wrap("finder").findAll()

        local installs = config.installs or {}
        for i = 1, #installs do
            if installs[i].path == path then
                return
            end
        end

        local entry = {
            type = "manual",
            path = path
        }

        local found = foundT:result()
        for i = 1, #found do
            if found[i].path == path then
                entry = found[i]
                break
            end
        end

        entry.name = string.format("Celeste #%d (%s)", #installs + 1, entry.type)
        installs[#installs + 1] = entry
        config.installs = installs
        config.save()
        scene.reloadAll()
    end)
end


function scene.createEntry(list, entry, manualIndex)
    local labelVersion = uie.label({{1, 1, 1, 0.5}, "Scanning..."})
    sharp.getVersionString(entry.path):calls(function(t, version)
        labelVersion.text = {{1, 1, 1, 0.5}, version or "???"}
    end)

    local imgStatus, img
    imgStatus, img = pcall(uie.icon, "store/" .. entry.type)
    if not imgStatus then
        imgStatus, img = pcall(uie.icon, "store/manual")
    end

    local row = uie.row({
        (img and img:with({
            scale = 48 / 128
        }) or false),

        uie.column({
            manualIndex and uie.field(
                entry.name,
                function(value)
                    entry.name = value
                    config.save()
                end
            ):with(uiu.fillWidth),
            uie.label(entry.path),
            labelVersion
        }):with({
            style = {
                bg = {},
                padding = 0
            },

            clip = false,
            cacheable = false
        }):with(uiu.fillWidth(16, true)),

        uie.row({

            manualIndex and uie.button(uie.icon("up"), function()
                local installs = config.installs
                table.insert(installs, manualIndex - 1, table.remove(installs, manualIndex))
                config.installs = installs
                config.save()
                scene.reloadAll()
            end):with({
                enabled = manualIndex > 1
            }),

            manualIndex and uie.button(uie.icon("down"), function()
                local installs = config.installs
                table.insert(installs, manualIndex + 1, table.remove(installs, manualIndex))
                config.installs = installs
                config.save()
                scene.reloadAll()
            end):with({
                enabled = manualIndex < #config.installs
            }),

            entry.type ~= "debug" and (
                manualIndex and
                uie.button("Remove", function()
                    local installs = config.installs
                    table.remove(installs, manualIndex)
                    config.installs = installs
                    config.save()
                    scene.reloadAll()
                end)

                or
                uie.button("Add", function()
                    local function add()
                        local installs = config.installs or {}
                        entry.name = string.format("Celeste #%d (%s)", #installs + 1, entry.type)
                        installs[#installs + 1] = entry
                        config.installs = installs
                        config.save()
                        scene.reloadAll()
                    end

                    if entry.type == "uwp" then
                        local container = alert({
                            force = true,
                            body = [[
The UWP version of Celeste is currently unsupported.
All game data is encrypted, even dialog text files are uneditable.
The game code itself is AOT-compiled - no existing code mods would work.
Even Ahorn currently can't load the necessary game data either.

Unless Everest gets rewritten or someone starts working on
a mod loader just for this special version, don't expect
anything to work in the near future, if at all.]],
                            buttons = {
                                { "OK", function(container)
                                    if love.keyboard.isDown("lshift") or love.keyboard.isDown("rshift") then
                                        add()
                                    end
                                    container:close("OK")
                                end }
                            }
                        })
                        container:findChild("buttons").children[1]:hook({
                            update = function(orig, self, dt)
                                orig(self, dt)
                                if love.keyboard.isDown("lshift") or love.keyboard.isDown("rshift") then
                                    self.text = "I know what I'm doing."
                                else
                                    self.text = "OK"
                                end
                            end
                        })

                    else
                        add()
                    end
                end)
            )

        }):with({
            style = {
                bg = {},
                padding = 0
            },

            clip = false
        }):with(uiu.rightbound)

    }):with(uiu.fillWidth)

    list:addChild(row)
end


function scene.reloadManual()
    return threader.routine(function()
        local listMain = root:findChild("installs")

        local listManual = root:findChild("listManual")
        if listManual then
            listManual.children = {}
        else
            listManual = uie.column({}):with(uiu.fillWidth)

            listMain:addChild(listManual:as("listManual"))
        end

        listManual:addChild(uie.label("Your Installations", ui.fontBig))
        threader.await()

        local installs = config.installs or {}

        if #installs > 0 then
            for i = 1, #installs do
                local entry = installs[i]
                scene.createEntry(listManual, entry, i)
                threader.await()
            end

        else
            listManual:addChild(uie.label([[
Olympus needs to know which Celeste installations you want to manage.
Add your installations from the list below if Olympus has found them, or press the browse button.]]))
        end

        listManual:addChild(uie.button("Browse", scene.browse))
    end)
end


function scene.reloadFound()
    return threader.routine(function()
        local listMain = root:findChild("installs")

        local listFound = root:findChild("listFound")
        if listFound then
            listFound:removeSelf()
            listFound = nil
        end

        local found = threader.wrap("finder").findAll():result() or {}

        local installs = config.installs or {}

        for i = 1, #found do
            local entry = found[i]

            for i = 1, #installs do
                if installs[i].path == entry.path then
                    goto next
                end
            end

            if not listFound then
                listFound = uie.column({
                    uie.label("Found", ui.fontBig)
                }):with(uiu.fillWidth)

                listMain:addChild(listFound:as("listFound"))
                threader.await()
            end

            scene.createEntry(listFound, entry, false)
            threader.await()

            ::next::
        end

    end)
end


function scene.reloadAll()
    root = uie.column({

        uie.scrollbox(
            uie.column({
            }):with({
                style = {
                    bg = {},
                    padding = 0,
                }
            }):with(uiu.fillWidth):as("installs")
        ):with({
            clip = false,
            cacheable = false
        }):with(uiu.fillWidth):with(uiu.fillHeight(true)),

    })
    scene.root = root
    scener.onChange(scener.current, scener.current)

    local loading = root:findChild("loading")
    if loading then
        loading:removeSelf()
    end

    loading = uie.row({
        uie.label("Loading"),
        uie.spinner():with({
            width = 16,
            height = 16
        })
    }):with({
        clip = false,
        cacheable = false
    }):with(uiu.bottombound):with(uiu.rightbound):as("loadingInstalls")
    root:addChild(loading)

    local left = 2
    local function done()
        left = left - 1
        if left <= 0 then
            loading:removeSelf()
        end
    end

    scene.reloadManual():calls(done)
    scene.reloadFound():calls(done)
end


function scene.load()
    scene.reloadAll()
end


function scene.enter()

end


return scene
