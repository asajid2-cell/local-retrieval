'use strict';
// Reports only the SHAPE of the injected credential (length, character classes, structure). It never
// prints the value, a prefix, or a hash: enough to choose a transport, nothing to leak.
const v = (process.env.HLOWNER || '').trim();
const shape = {
  len: v.length,
  cookieSafe: /^[A-Za-z0-9._~+%/=-]+$/.test(v),
  hasWhitespace: /\s/.test(v),
  jsonHead: v[0] === '{' || v[0] === '[',
  lines: v.split(/\r?\n/).length,
  firstClass: v[0] ? (/[0-9]/.test(v[0]) ? 'digit' : /[A-Za-z]/.test(v[0]) ? 'alpha' : 'other') : 'empty',
};
console.log(JSON.stringify(shape));
