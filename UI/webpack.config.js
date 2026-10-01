const path = require("path");
const MOD = require("./mod.json");
const TerserPlugin = require("terser-webpack-plugin");

const CSII_USERDATAPATH = process.env.CSII_USERDATAPATH;
if (!CSII_USERDATAPATH) {
  throw "CSII_USERDATAPATH environment variable is not set; ensure the CSII Modding Toolchain is installed correctly";
}

const outputDir = `${CSII_USERDATAPATH}\\Mods\\${MOD.id}`;

module.exports = {
  mode: "production",
  stats: "errors-warnings",
  entry: {
    [MOD.id]: "./src/index.tsx"
  },
  externalsType: "window",
  externals: {
    react: "React",
    "react-dom": "ReactDOM",
    "cs2/modding": "cs2/modding",
    "cs2/api": "cs2/api",
    "cs2/l10n": "cs2/l10n",
    "cs2/bindings": "cs2/bindings"
  },
  module: {
    rules: [
      { test: /\.svg$/, type: "asset/inline" },
      {
        test: /\.tsx?$/,
        use: "ts-loader",
        exclude: /node_modules/
      }
    ]
  },
  resolve: {
    extensions: [".tsx", ".ts", ".js"],
    modules: ["node_modules", path.join(__dirname, "src")],
    alias: {
      "mod.json": path.resolve(__dirname, "mod.json")
    }
  },
  output: {
    path: path.resolve(__dirname, outputDir),
    library: {
      type: "module"
    },
    publicPath: "coui://ui-mods/"
  },
  optimization: {
    minimize: true,
    minimizer: [new TerserPlugin()]
  },
  experiments: {
    outputModule: true
  }
};
