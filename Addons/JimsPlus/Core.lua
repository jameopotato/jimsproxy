local ADDON_NAME, namespace = ...

namespace.BUILD = 12

print("|cFF00FF00[JimsPlus]|r Core loaded (build " .. namespace.BUILD .. ")")

namespace.modules = {}

function namespace:RegisterModule(name, initFunc)
    self.modules[name] = { init = initFunc, enabled = true }
end

-- Module enable/disable config UI is deferred.
-- For now all modules are always enabled.
