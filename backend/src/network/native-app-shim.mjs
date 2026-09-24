import path from "node:path";

const resources = () => path.resolve(process.env.BRISA_RESOURCE_DIR || path.join(process.cwd(), "resources"));

export const app = Object.freeze({
  getAppPath: () => process.cwd(),
  getVersion: () => process.env.BRISA_VERSION || "0.0.0-dev",
  getPath: name => name === "userData"
    ? path.resolve(process.env.BRISA_DATA_DIR || path.join(process.env.LOCALAPPDATA || process.cwd(), "Brisa"))
    : resources(),
});
