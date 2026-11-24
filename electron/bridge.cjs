'use strict';

const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');

class Bridge {
  constructor(executable, args, env = process.env) {
    this.nextId = 0;
    this.pending = new Map();
    this.child = spawn(executable, args, { env, windowsHide: true, stdio: ['pipe', 'pipe', 'inherit'] });
    this.child.on('error', error => this.disconnect(error));
    this.child.on('exit', () => this.disconnect(new Error('Служба виджета завершилась.')));
    this.child.stdin.on('error', error => this.disconnect(error));

    createInterface({ input: this.child.stdout }).on('line', line => {
      let response;
      try {
        response = JSON.parse(line);
      } catch {
        this.disconnect(new Error('Некорректный ответ службы виджета.'));
        return;
      }
      const pending = this.pending.get(response.id);
      if (!pending) return;
      this.pending.delete(response.id);
      clearTimeout(pending.timeout);
      if (response.error) pending.reject(new Error(response.error));
      else pending.resolve(response.result);
    });
  }

  disconnect(error) {
    this.error = error;
    for (const { reject, timeout } of this.pending.values()) {
      clearTimeout(timeout);
      reject(error);
    }
    this.pending.clear();
  }

  request(method, params = {}) {
    if (this.error) return Promise.reject(this.error);
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error('Служба виджета не ответила вовремя.'));
      }, 15_000);
      this.pending.set(id, { resolve, reject, timeout });
      this.child.stdin.write(JSON.stringify({ ...params, id, method }) + '\n');
    });
  }

  close() {
    this.child.stdin.end();
  }
}

module.exports = Bridge;
