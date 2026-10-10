# Pass4 attributes compatibility

This implementation policy applies canonical Annex 233.1C to optional descriptive JSON. It adds no stock, cost, price, supplier, warranty or physical identity authority.

Version 1 remains the legacy primitive-extension profile: existing valid object keys and primitive values remain editable. Nested objects/arrays, malformed JSON and duplicate names (case insensitive) are rejected. Version 2 supports optional `profile`: `general`, `fan`, `cable`, `bulb`. No profile or specification is mandatory. Known specifications work without a profile; when a specific profile is supplied, incompatible profile fields are rejected. Unknown v2 keys require the `x_` extension prefix and primitive values. Unsupported future versions fail closed.

| Keys (aliases) | Unit | Inclusive range | Profile |
|---|---|---|---|
| wattage / ratedWattage | W | 0.01–100000 | general |
| voltage / ratedVoltage | V | 0.01–100000 | general |
| frequency | Hz | 0.01–10000 | general |
| sweep | mm | 0.01–10000 | fan |
| blade_count / bladeCount | whole count | 1–100 | fan |
| cores / coreCount | whole count | 1–1000 | cable |
| roll_length / length | m | 0.001–1000000 | cable |
| conductor_cross_section / conductorCrossSection | mm² | 0.001–10000 | cable |
| color_temperature / colorTemperature | whole K | 1000–20000 | bulb |

These are declared bounded implementation ranges, not a requirement for every product to contain every property. `color`, `brand`, `insulation`, `socket_type` / `socketType` require nonempty strings up to 120 characters; socket type is bulb-specific when a specific profile is declared. Key names are case insensitive, consist of ASCII letters/digits/underscore/hyphen and have at most 60 characters. Legacy version 1 does not reinterpret a legacy descriptive value as a typed v2 specification. An operator must explicitly opt into version 2.
