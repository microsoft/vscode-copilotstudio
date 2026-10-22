const { execFileSync } = require('node:child_process');

const stopProcessTree = (processId) => {
  if (!processId) {
    return;
  }

  if (process.platform === 'win32') {
    try {
      execFileSync('taskkill', ['/PID', String(processId), '/F', '/T'], { stdio: 'ignore', windowsHide: true });
    } catch {
    }

    return;
  }

  try {
    process.kill(-processId, 'SIGKILL');
    return;
  } catch {
  }

  try {
    process.kill(processId, 'SIGKILL');
  } catch {
  }
};

module.exports = { stopProcessTree };
