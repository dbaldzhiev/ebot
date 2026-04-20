const http = require('http');

const BASE_URL = 'http://localhost:5000/api';

function request(method, path) {
  return new Promise((resolve, reject) => {
    const url = `${BASE_URL}${path}`;
    const options = {
      method: method,
      timeout: 5000
    };

    const req = http.request(url, options, (res) => {
      let data = '';
      res.on('data', (chunk) => data += chunk);
      res.on('end', () => {
        if (res.statusCode >= 200 && res.statusCode < 300) {
          resolve(data);
        } else {
          reject(new Error(`Status: ${res.statusCode} - ${data}`));
        }
      });
    });

    req.on('error', reject);
    req.on('timeout', () => {
      req.destroy();
      reject(new Error('Request timed out'));
    });
    req.end();
  });
}

const command = process.argv[2];

(async () => {
  try {
    if (command === 'state') {
      const data = await request('GET', '/debug/state');
      console.log(JSON.stringify(JSON.parse(data), null, 2));
    } else if (command === 'step') {
      await request('POST', '/debug/step');
      console.log('Success: Single tick executed.');
    } else if (command === 'status') {
      const data = await request('GET', '/status');
      console.log(JSON.stringify(JSON.parse(data), null, 2));
    } else {
      console.log('Usage: node ebot_api.js [state|step|status]');
    }
  } catch (err) {
    process.stderr.write(`Error: ${err.message}\n`);
    process.exit(1);
  }
})();
