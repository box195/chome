import {readFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {build} from 'esbuild';
const root=fileURLToPath(new URL('../',import.meta.url));
let source=await readFile(new URL('../chome.js',import.meta.url),'utf8');
for(const name of ['app','auth','database'])source=source.replaceAll(`https://www.gstatic.com/firebasejs/11.10.0/firebase-${name}.js`,`firebase/${name}`);
await build({stdin:{contents:source,resolveDir:root,sourcefile:'chome.js'},bundle:true,minify:true,format:'esm',platform:'browser',target:'es2022',outfile:new URL('../chome.bundle.js',import.meta.url).pathname.replace(/^\/([A-Z]:)/i,'$1'),legalComments:'eof'});
