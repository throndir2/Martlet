import { LIMITS, ModelDescription } from "./assets.js";
import { boundedInteger, Diagnostic, finite, requireCondition } from "./diagnostics.js";
import { CubismModel } from "./sdk.js";

export type Aspect = "mouth" | "expression" | "gaze" | "head" | "body" | "secondaryMotion";

export interface Parameter {
  readonly id: string;
  readonly index: number;
  readonly minimum: number;
  readonly maximum: number;
  readonly neutral: number;
  readonly groups: readonly ("LipSync" | "EyeBlink")[];
}

/** Names refer to the trusted host's composed channels, not raw provider output or parameter aliases. */
export interface ChannelMapping {
  readonly channel: string;
  readonly parameterId: string;
  readonly aspect: Aspect;
  readonly outputMinimum: number;
  readonly outputMaximum: number;
}

export interface Capabilities {
  readonly parameters: readonly Parameter[];
  readonly mappings: readonly ChannelMapping[];
  readonly unmappedParameters: readonly string[];
  readonly diagnostics: readonly Diagnostic[];
  readonly supportedReductions: readonly string[];
}

export function inspectParameters(model: CubismModel, description: ModelDescription): readonly Parameter[] {
  const count = model.getParameterCount();
  boundedInteger(count, LIMITS.parameters, "parameter count");
  const ids = new Set<string>();
  return Object.freeze(Array.from({ length: count }, (_, index) => {
    const id = model.getParameterId(index).getString().s;
    requireCondition(typeof id === "string" && id.length > 0 && id.length <= 256 && !ids.has(id),
      "INVALID_MODEL_PARAMETERS", "Core returned invalid or duplicate parameter IDs.");
    ids.add(id);
    const minimum = model.getParameterMinimumValue(index);
    const maximum = model.getParameterMaximumValue(index);
    const neutral = model.getParameterDefaultValue(index);
    [minimum, maximum, neutral].forEach(value => finite(value, id));
    requireCondition(minimum <= neutral && neutral <= maximum,
      "INVALID_MODEL_PARAMETERS", `Core returned inconsistent bounds for ${id}.`);
    const groups: ("LipSync" | "EyeBlink")[] = [];
    if (description.groups.lipSync.includes(id)) groups.push("LipSync");
    if (description.groups.eyeBlink.includes(id)) groups.push("EyeBlink");
    return Object.freeze({ id, index, minimum, maximum, neutral, groups: Object.freeze(groups) });
  }));
}

export class MappingPlan {
  readonly capabilities: Capabilities;
  readonly #entries: readonly { mapping: ChannelMapping; parameter: Parameter }[];

  constructor(parameters: readonly Parameter[], description: ModelDescription, mappings: readonly ChannelMapping[]) {
    boundedInteger(mappings.length, LIMITS.parameters, "mapping count");
    const targetIds = new Set<string>();
    const diagnostics = [...description.diagnostics];
    const byId = new Map(parameters.map(p => [p.id, p]));
    for (const [name, ids] of Object.entries(description.groups)) {
      if (ids.length === 0) diagnostics.push({ code: "GROUP_NOT_DECLARED", message: `${name} group is not declared.` });
      for (const id of ids) if (!byId.has(id)) diagnostics.push({
        code: "ABSENT_GROUP_PARAMETER", message: `${name} declares ${id}, but Core reports no such parameter.`,
      });
    }
    const entries: { mapping: ChannelMapping; parameter: Parameter }[] = [];
    for (const input of mappings) {
      requireCondition(typeof input.channel === "string" &&
        /^(semantics|blendshapes|local)\.[A-Za-z][A-Za-z0-9_]{0,127}$/.test(input.channel),
      "INVALID_MAPPING", "Channel must be an explicit semantics.*, blendshapes.* or local.* host channel.");
      requireCondition(["mouth", "expression", "gaze", "head", "body", "secondaryMotion"].includes(input.aspect),
        "INVALID_MAPPING", "Mapping must have an explicit supported aspect.");
      finite(input.outputMinimum, "outputMinimum");
      finite(input.outputMaximum, "outputMaximum");
      requireCondition(!targetIds.has(input.parameterId), "DUPLICATE_WRITER",
        `More than one mapping targets ${input.parameterId}; compose upstream instead.`);
      targetIds.add(input.parameterId);
      const parameter = byId.get(input.parameterId);
      if (!parameter) {
        diagnostics.push({ code: "ABSENT_MAPPING_PARAMETER", message: `${input.parameterId} is absent; mapping is disabled.` });
        continue;
      }
      requireCondition(input.outputMinimum >= parameter.minimum && input.outputMinimum <= parameter.maximum &&
        input.outputMaximum >= parameter.minimum && input.outputMaximum <= parameter.maximum,
      "INVALID_MAPPING_RANGE", `Mapping range for ${parameter.id} exceeds Core-reported bounds.`);
      entries.push({ mapping: Object.freeze({ ...input }), parameter });
    }
    this.#entries = entries;
    const mapped = new Set(entries.map(e => e.parameter.id));
    const unmapped = parameters.filter(p => !mapped.has(p.id)).map(p => p.id);
    for (const parameter of parameters) {
      if (!mapped.has(parameter.id) && parameter.groups.length) diagnostics.push({
        code: "UNMAPPED_GROUP_PARAMETER", message: `${parameter.id} is declared but has no explicit mapping.`,
      });
    }
    this.capabilities = Object.freeze({
      parameters, mappings: Object.freeze(entries.map(e => e.mapping)),
      unmappedParameters: Object.freeze(unmapped),
      diagnostics: Object.freeze(diagnostics.map(d => Object.freeze(d))),
      supportedReductions: Object.freeze([
        "Explicit single normalized channel -> parameter affine mapping (including inversion).",
        "Explicit fan-out of one composed channel to multiple distinct parameters.",
        "No automatic ARKit, viseme, group, procedural, or many-to-one reduction.",
      ]),
    });
  }

  resolve(channels: Readonly<Record<string, number>>): {
    writes: readonly { index: number; value: number }[];
    unmappedChannels: readonly string[];
  } {
    requireCondition(channels !== null && typeof channels === "object" && !Array.isArray(channels),
      "INVALID_CHANNELS", "Composed channels must be a finite normalized value record.");
    const entries = Object.entries(channels);
    boundedInteger(entries.length, LIMITS.parameters, "frame channels");
    for (const [channel, value] of entries) {
      requireCondition(channel.length <= 140 && typeof value === "number" &&
        Number.isFinite(value) && value >= 0 && value <= 1,
      "INVALID_CHANNEL_VALUE", `${channel} must be in 0..1; invalid frames are rejected atomically.`);
    }
    const known = new Set(this.#entries.map(e => e.mapping.channel));
    const writes = this.#entries.map(({ mapping, parameter }) => {
      const value = Object.hasOwn(channels, mapping.channel) ? channels[mapping.channel] : undefined;
      return {
        index: parameter.index,
        value: value === undefined ? parameter.neutral :
          Math.max(parameter.minimum, Math.min(parameter.maximum,
            mapping.outputMinimum * (1 - value) + mapping.outputMaximum * value)),
      };
    });
    return { writes, unmappedChannels: entries.map(([key]) => key).filter(key => !known.has(key)) };
  }
}
