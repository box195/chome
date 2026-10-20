import {execFileSync} from 'node:child_process';
import {writeFileSync} from 'node:fs';
import {fileURLToPath} from 'node:url';
import webpush from 'web-push';
const keys=webpush.generateVAPIDKeys();
execFileSync(fileURLToPath(new URL('./ProtectVapid.exe',import.meta.url)),[],{input:JSON.stringify({...keys,subject:'https://box195.github.io/chome/'}),windowsHide:true,stdio:['pipe','pipe','pipe']});
writeFileSync(fileURLToPath(new URL('../../push-config.json',import.meta.url)),JSON.stringify({enabled:true,publicKey:keys.publicKey},null,2));
console.log('Private key protected on this PC; only public push-config.json changed.');
