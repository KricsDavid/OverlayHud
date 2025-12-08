const { spawn } = require("child_process");
const path = require("path");

const serverPath = path.join(__dirname, "server.js");

function start(label, cmd, args, cwd) {
  const child = spawn(cmd, args, {
    cwd,
    stdio: "inherit",
    env: process.env,
  });
  child.on("close", (code) => {
    console.log(`[${label}] exited with code ${code}`);
  });
  return child;
}

console.log("[startall] launching backend...");
start("server", "node", [serverPath], path.join(__dirname));

