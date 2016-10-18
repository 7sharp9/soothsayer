"use strict";

exports.__esModule = true;
exports.Soothsayer = undefined;

var _Promise = require("../node_modules/fable-powerpack/./Promise");

var _String = require("fable-core/String");

var Soothsayer = exports.Soothsayer = function ($exports) {
    var test = $exports.test = function (builder_) {
        return builder_.Delay(function () {
            return Promise.resolve("");
        });
    }(_Promise.PromiseImpl.promise);

    (0, _String.fsFormat)("Hello")(function (x) {
        console.log(x);
    });
    return $exports;
}({});
//# sourceMappingURL=soothsayer.js.map