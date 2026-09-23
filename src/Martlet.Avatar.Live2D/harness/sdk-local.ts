import { CubismFramework } from "../local-sdk/Framework/src/live2dcubismframework";
import { CubismMoc } from "../local-sdk/Framework/src/model/cubismmoc";
import { CubismRenderer_WebGL } from "../local-sdk/Framework/src/rendering/cubismrenderer_webgl";
import { CubismMatrix44 } from "../local-sdk/Framework/src/math/cubismmatrix44";
import { FRAMEWORK_REVISION, type SdkModules } from "../lib/sdk.js";

export const sdk: SdkModules = {
  revision: FRAMEWORK_REVISION,
  CubismFramework, CubismMoc, CubismRenderer_WebGL, CubismMatrix44,
};
