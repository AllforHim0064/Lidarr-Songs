'use strict';
const $ = (s) => document.querySelector(s);
const escape = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const state = {user:null,tracks:[],artists:[],requests:[],playlists:[],view:'home',filter:'all',query:'',error:'',queue:[],current:-1,shuffle:false,repeat:false};
const audio = $('#audio');
audio.volume = 0.8;
let toastTimer, refreshTimer, searchTimer, dragged;
function toast(message) { $('#toast').textContent=message; $('#toast').hidden=false; clearTimeout(toastTimer); toastTimer=setTimeout(()=>$('#toast').hidden=true,5000); }
async function api(path, data) {
  const response=await fetch('/api/'+path,{method:data?'POST':'GET',headers:data?{'Content-Type':'application/json','X-CSRF-Token':state.user?.csrf||''}:{},body:data?JSON.stringify(data):undefined});
  const result=await response.json();
  if(!response.ok) { if(response.status===401 && path!=='login') showLogin(); throw Error(result.error||'The request failed.'); }
  return result;
}
function showLogin() { state.user=null; $('#shell').hidden=true; $('#login').hidden=false; clearInterval(refreshTimer); audio.pause(); }
async function start(user) {
  state.user=user; $('#login').hidden=true; $('#shell').hidden=false;
  $('#profile-name').textContent=user.name; $('#avatar').textContent=user.name[0].toUpperCase(); $('#admin-nav').hidden=user.role!=='admin';
  $('#content').innerHTML='<div class="loading">Finding your frequency…</div>';
  await load(); clearInterval(refreshTimer);
  refreshTimer=setInterval(async()=>{try{state.requests=await api('requests');state.playlists=await api('playlists');if(state.view==='requests') render();navigation();}catch(_){}},45000);
}
async function load(force=false) {
  try {
    state.error=''; const data=await api('catalog'+(force?'?refresh=1':'')); state.tracks=data.tracks; state.artists=data.artists;
    [state.requests,state.playlists]=await Promise.all([api('requests'),api('playlists')]);
  } catch(error) {state.error=error.message;}
  render(); navigation();
}
function navigation() {
  document.querySelectorAll('nav button').forEach(b=>b.classList.toggle('active',b.dataset.view===state.view));
  $('#request-count').textContent=state.requests.filter(r=>!['available','declined'].includes(r.status)).length;
  $('#playlist-nav').innerHTML=state.playlists.map(p=>`<button class="playlist-link ${state.view==='playlist:'+p.id?'active':''}" data-playlist="${p.id}"><i>♫</i>${escape(p.name)}</button>`).join('');
  const names={home:'Discover',library:'Your library',requests:'Requests',admin:'Manage'};
  $('#view-label').textContent=names[state.view]||'Your playlist';
}
function art(track, mini=false) {return `<div class="${mini?'mini-art':'art'}"><span class="art-fallback">♫</span>${track?.cover?`<img src="${escape(track.cover)}" alt="" loading="lazy">`:''}</div>`;}
function time(seconds) {if(!Number.isFinite(seconds))return '0:00';return Math.floor(seconds/60)+':'+String(Math.floor(seconds%60)).padStart(2,'0');}
function tracksForView() {
  let tracks=state.view.startsWith('playlist:')?(state.playlists.find(p=>p.id===Number(state.view.split(':')[1]))?.tracks||[]):state.tracks;
  const q=state.query.toLowerCase().trim();
  if(q) tracks=tracks.filter(t=>`${t.title} ${t.artist} ${t.album}`.toLowerCase().includes(q));
  if(state.view==='library'&&state.filter==='available')tracks=tracks.filter(t=>t.hasFile);
  if(state.view==='library'&&state.filter==='missing')tracks=tracks.filter(t=>!t.hasFile);
  return tracks;
}
function status(track) {
  if(track.hasFile)return '<span class="status available">Available</span>';
  const request=state.requests.find(r=>r.track.identity===track.identity&&r.status!=='declined');
  return request?`<span class="status ${escape(request.status)}">${escape(request.status)}</span>`:'<span class="muted">Not requested</span>';
}
function table(tracks, playlist=false) {
  if(!tracks.length)return empty('Nothing here. Yet.','Search your library, add a song to a playlist, or ask your admin to add an artist.');
  return `<table class="track-table"><thead><tr><th>#</th><th>SONG</th><th class="album-column">ALBUM</th><th class="duration-column">TIME</th><th></th></tr></thead><tbody>${tracks.map((t,i)=>`<tr ${playlist&&!state.query?`draggable="true" class="drag-row" data-identity="${escape(t.identity)}"`:''}><td>${i+1}</td><td><div class="song-cell">${art(t,true)}<div><strong>${escape(t.title)}</strong><small>${escape(t.artist)}</small></div></div></td><td class="album-column muted">${escape(t.album)}</td><td class="duration-column muted">${time(t.duration/1000)}</td><td><div class="track-actions">${t.hasFile?`<button data-play="${t.id}" aria-label="Play ${escape(t.title)}">▶</button>`:state.requests.some(r=>r.track.identity===t.identity&&r.status!=='declined')?status(t):`<button data-request="${t.id}">↗ Request</button>`}<button data-add="${t.id}" title="Add to playlist" aria-label="Add ${escape(t.title)} to playlist">＋</button>${playlist?`<button data-remove="${escape(t.identity)}" title="Remove from playlist" aria-label="Remove ${escape(t.title)}">×</button>`:''}</div></td></tr>`).join('')}</tbody></table>`;
}
function empty(title, description, action='') {return `<div class="empty"><span class="symbol">◒</span><strong>${escape(title)}</strong><p>${escape(description)}</p>${action}</div>`;}
function card(track) {return `<button class="album-card" data-album="${track.albumId}"><span class="card-tag">${track.hasFile?'IN YOUR LIBRARY':'MAKE IT YOURS'}</span>${art(track)}<h3>${escape(track.album)}</h3><p>${escape(track.artist)}${track.year?' · '+escape(track.year):''}</p></button>`;}
function heading(title,description,action='') {return `<div class="section-heading"><div><h2>${escape(title)}</h2><p>${escape(description)}</p></div>${action}</div>`;}
function render() {
  const banner=state.error?`<div class="error-banner">${escape(state.error)}</div>`:'';
  if(state.query&&!state.view.startsWith('playlist:')&&state.view!=='admin') {
    $('#content').innerHTML=banner+heading('Search results',`${tracksForView().length} songs matching “${state.query}”`)+table(tracksForView());navigation();return;
  }
  let html='';
  if(state.view==='home') {
    const available=state.tracks.filter(t=>t.hasFile).length;
    const albums=[...new Map(state.tracks.map(t=>[t.albumId,t])).values()].slice(0,4);
    html=`<section class="hero"><div class="hero-copy"><span class="eyebrow">LESS SCROLLING. MORE LISTENING.</span><h1>Your next favorite<br>is <em>waiting here.</em></h1><p>A place for the songs you love, the ones you’re missing, and everything in between.</p><button class="primary" data-view="library">Explore your music <span>↗</span></button></div><div class="hero-art" aria-hidden="true"><div class="record"></div><div class="sleeve"><strong>YOUR<br>OWN<br>FREQUENCY.</strong><small>VOLUME 01 / CADENCE</small></div></div></section>`;
    html+=`<div class="stats-strip"><div class="stat"><span>♫</span><div><strong>${available.toLocaleString()}</strong><small>SONGS READY TO PLAY</small></div></div><div class="stat"><span>↗</span><div><strong>${state.requests.filter(r=>!['available','declined'].includes(r.status)).length}</strong><small>REQUESTS IN MOTION</small></div></div><div class="stat"><span>☷</span><div><strong>${state.playlists.length}</strong><small>PLAYLISTS MADE BY YOU</small></div></div></div>`;
    html+=heading('On your radar','Albums from your personal catalog','<button data-view="library">See all ↗</button>')+(albums.length?`<div class="cards">${albums.map(card).join('')}</div>`:empty('Start with an artist.','Your catalog comes from Lidarr. Add an artist in Manage, then refresh after its metadata loads.',state.user.role==='admin'?'<button class="primary" data-view="admin">Add your first artist ↗</button>':''));
    html+=heading('Made for the moment','Your playlists. No algorithm required.','<button data-create-playlist>＋ New playlist</button>');
    html+=state.playlists.length?`<div class="playlist-grid">${state.playlists.slice(0,3).map(p=>`<button class="album-card" data-playlist="${p.id}"><div class="art"><span>${escape(p.name)}</span></div><h3>${escape(p.name)}</h3><p>${p.tracks.length} songs · By you</p></button>`).join('')}</div>`:empty('A soundtrack of your own.','Morning drives, late nights, or whatever’s on repeat. Give it a name and start adding songs.','<button class="primary" data-create-playlist>Create a playlist ＋</button>');
  } else if(state.view==='library') {
    html='<h1 class="page-title">Your library.</h1><p class="page-subtitle">All your songs, in one place. Request what’s missing. Play what’s yours.</p><div class="tabs">'+[['all','All songs'],['available','Ready to play'],['missing','Not downloaded']].map(([id,name])=>`<button class="${state.filter===id?'active':''}" data-filter="${id}">${name}</button>`).join('')+'<button data-create-playlist>＋ New playlist</button></div>'+table(tracksForView());
  } else if(state.view==='requests') {
    html='<h1 class="page-title">Good things are coming.</h1><p class="page-subtitle">'+(state.user.role==='admin'?'Review requests, approve them, and choose a download source.':'Follow your song requests from wish list to library.')+'</p>';
    html+=state.requests.length?`<table class="track-table"><thead><tr><th></th><th>REQUESTED SONG</th><th class="album-column">REQUESTED BY</th><th>STATUS</th><th></th></tr></thead><tbody>${state.requests.map(r=>`<tr><td>↗</td><td><div class="song-cell">${art(r.track,true)}<div><strong>${escape(r.track.title)}</strong><small>${escape(r.track.artist)}${r.note?' · '+escape(r.note):''}</small></div></div></td><td class="album-column muted">${escape(r.user)}</td><td><span class="status ${escape(r.status)}">${escape(r.status)}</span></td><td><div class="track-actions">${r.status==='available'?`<button data-play="${r.track.id}">▶ Play</button>`:state.user.role==='admin'&&r.status!=='declined'?`${r.status==='pending'?`<button data-approve="${r.id}">Approve</button>`:`<button data-sources="${r.track.id}">Find sources</button>`}<button data-decline="${r.id}">Decline</button>`:''}</div></td></tr>`).join('')}</tbody></table>`:empty('Your wish list starts here.','Find a missing song in the library and hit Request. Your admin will take it from there.','<button class="primary" data-view="library">Find a song ↗</button>');
  } else if(state.view.startsWith('playlist:')) {
    const p=state.playlists.find(p=>p.id===Number(state.view.split(':')[1]));
    if(!p){state.view='home';return render();}
    html=`<section class="playlist-hero"><div class="art">♫</div><div><span class="eyebrow">YOUR PLAYLIST</span><h1>${escape(p.name)}</h1><p>${escape(p.description||'Every song has a place here.')} · ${p.tracks.length} songs</p><button class="primary" data-playlist-play="${p.id}">▶ Play playlist</button></div></section><div class="tabs"><button data-rename="${p.id}">Rename</button><button data-export="${p.id}">Export M3U</button><button data-delete-playlist="${p.id}">Delete</button><span class="muted">Drag rows to change the order</span></div>`+table(tracksForView(),true);
  } else if(state.view==='admin') {
    html='<h1 class="page-title">Behind the music.</h1><p class="page-subtitle">Add listeners, expand your catalog, and see what’s downloading.</p><section class="admin-panel"><h3>Expand the catalog</h3><p class="page-subtitle">Search for an artist. Adding uses the first root folder and profiles in Lidarr, with album monitoring and automatic searches disabled.</p><form id="artist-search" class="inline-form"><input name="q" placeholder="Artist name" required aria-label="Artist name"><button class="primary">Search artists ↗</button></form><div id="artist-results"></div></section><section class="admin-panel"><h3>Invite a listener</h3><form id="user-create" class="inline-form"><input name="name" placeholder="Username" required aria-label="New username"><input name="password" type="password" placeholder="Password (10+ characters)" minlength="10" required autocomplete="new-password" aria-label="New user password"><button class="primary">Create account ＋</button></form><p class="page-subtitle">Give them the username, password, and this app’s address.</p></section><section class="admin-panel"><h3>Download activity</h3><button class="secondary" data-activity>Check Lidarr queue ↻</button><div id="activity"></div></section>';
  }
  $('#content').innerHTML=banner+html;navigation();
}
function modal(html) {$('#modal-content').innerHTML=html;if(!$('#modal').open)$('#modal').showModal();}
function createPlaylist(trackId) {
  modal(`<h2>A new soundtrack.</h2><p>Give your playlist a name. You can add downloaded songs and requests.</p><form id="playlist-create"><label>Name<input name="name" placeholder="e.g. After hours" required maxlength="100"></label><label>Description<textarea name="description" placeholder="What’s the mood?" maxlength="500"></textarea></label><button class="primary">Create playlist ＋</button></form>`);
  $('#playlist-create').onsubmit=async e=>{e.preventDefault();try {const data=Object.fromEntries(new FormData(e.target));state.playlists=await api('playlists',data);const p=state.playlists[0];if(trackId)state.playlists=await api('playlists',{action:'add',id:p.id,trackId});$('#modal').close();state.view='playlist:'+p.id;state.query='';$('#search').value='';render();toast('Playlist created. Make it yours.');}catch(error){toast(error.message);}};
}
function addToPlaylist(trackId) {
  if(!state.playlists.length)return createPlaylist(trackId);
  modal(`<h2>Find its place.</h2><p>Add this song to a playlist.</p><div>${state.playlists.map(p=>`<button class="playlist-link" data-add-to="${p.id}" data-track="${trackId}"><i>♫</i>${escape(p.name)} <small class="muted">${p.tracks.length} songs</small></button>`).join('')}</div><button class="secondary" data-create-for="${trackId}">＋ New playlist</button>`);
}
async function sources(trackId) {
  modal('<h2>Finding your song.</h2><p>Searching indexers and whole-album fallbacks. This can take a moment…</p>');
  try{const result=await api('sources?trackId='+trackId);if(!$('#modal').open)return;
    modal(`<h2>Choose a source.</h2><p>Only the requested song is imported. Album fallbacks may download the whole album; cleanup follows Lidarr’s seeding rules. Search results expire after 30 minutes.</p>${result.warnings.map(w=>`<p>${escape(w)}</p>`).join('')}${result.candidates.length?result.candidates.map(c=>`<div class="source-card"><div><strong>${escape(c.title)}</strong><small>${escape(c.source)} · ${escape(c.quality||'Unknown quality')} · ${(c.size/1048576).toFixed(1)} MB · ${c.albumFallback?'Album fallback':'Song result'}</small>${c.rejection?`<small class="rejection">${escape(c.rejection)}</small>`:''}</div><button class="primary" data-grab="${escape(c.id)}" data-track="${trackId}" ${c.rejection?'disabled':''}>Download ↗</button></div>`).join(''):empty('No matching sources.','Try another release, check your indexers, or configure slskd in Lidarr.')}`);
  }catch(error){modal('<h2>Search needs attention.</h2><p>'+escape(error.message)+'</p>');}
}
function play(trackId, queue=tracksForView()) {
  const track=state.tracks.find(t=>t.id===Number(trackId))||queue.find(t=>t.id===Number(trackId));
  if(!track?.hasFile)return toast('This song is not downloaded yet.');
  state.queue=queue.filter(t=>t.hasFile);if(!state.queue.some(t=>t.id===track.id))state.queue=[track];
  state.current=state.queue.findIndex(t=>t.id===track.id);startAudio();
}
function startAudio() {
  const track=state.queue[state.current];if(!track)return;
  audio.src='/api/stream/'+track.id;audio.play().catch(()=>toast('Playback could not start. Check the music mount and your browser’s audio format support.'));
  $('#now-playing').innerHTML=art(track,true)+`<div><strong>${escape(track.title)}</strong><small>${escape(track.artist)}</small></div>`;
}
function skip(direction=1) {
  if(!state.queue.length)return;
  if(direction<0&&audio.currentTime>3){audio.currentTime=0;return;}
  let next=state.shuffle&&direction>0?Math.floor(Math.random()*state.queue.length):state.current+direction;
  if(next>=state.queue.length&&!state.repeat){audio.pause();return;}
  state.current=(next+state.queue.length)%state.queue.length;startAudio();
}
async function action(event) {
  const b=event.target.closest('button');if(!b||b.disabled)return;
  const d=b.dataset;
  try {
    if(d.view){state.view=d.view;state.query='';$('#search').value='';render();}
    if(d.playlist){state.view='playlist:'+d.playlist;state.query='';$('#search').value='';render();}
    if(d.filter){state.filter=d.filter;render();}
    if(d.album){const tracks=state.tracks.filter(t=>t.albumId===Number(d.album));modal(`<h2>${escape(tracks[0]?.album)}</h2><p>${escape(tracks[0]?.artist)}</p>`+table(tracks));}
    if(d.play)play(Number(d.play));
    if(d.request){b.disabled=true;state.requests=await api('requests',{trackId:Number(d.request)});render();$('#modal').close();toast('Request sent. Your admin will review it.');}
    if(d.add)addToPlaylist(Number(d.add));
    if(d.createPlaylist!==undefined)createPlaylist();
    if(d.createFor)createPlaylist(Number(d.createFor));
    if(d.addTo){state.playlists=await api('playlists',{action:'add',id:Number(d.addTo),trackId:Number(d.track)});$('#modal').close();render();toast('Added to your playlist.');}
    if(d.remove){state.playlists=await api('playlists',{action:'remove',id:Number(state.view.split(':')[1]),identity:d.remove});render();}
    if(d.approve){state.requests=await api('requests',{action:'approve',id:Number(d.approve)});render();const r=state.requests.find(r=>r.id===Number(d.approve));await sources(r.track.id);}
    if(d.decline){modal('<h2>Decline this request?</h2><form id="decline-form"><label>Optional note<input name="note" maxlength="500" placeholder="Let the listener know why"></label><button class="primary">Decline request</button></form>');$('#decline-form').onsubmit=async e=>{e.preventDefault();try{state.requests=await api('requests',{action:'decline',id:Number(d.decline),note:new FormData(e.target).get('note')});$('#modal').close();render();}catch(error){toast(error.message);}};}
    if(d.sources)await sources(Number(d.sources));
    if(d.grab){b.disabled=true;await api('sources',{trackId:Number(d.track),candidateId:d.grab});state.requests=await api('requests');$('#modal').close();render();toast('Download queued in Lidarr.');}
    if(d.playlistPlay){const p=state.playlists.find(p=>p.id===Number(d.playlistPlay));const t=p.tracks.find(t=>t.hasFile);if(t)play(t.id,p.tracks);else toast('Request these songs first; none are downloaded yet.');}
    if(d.rename){const p=state.playlists.find(p=>p.id===Number(d.rename));modal(`<h2>Rename your playlist.</h2><form id="rename-form"><input name="name" required maxlength="100" value="${escape(p.name)}" aria-label="Playlist name"><button class="primary">Save name</button></form>`);$('#rename-form').onsubmit=async e=>{e.preventDefault();try{state.playlists=await api('playlists',{action:'rename',id:p.id,name:new FormData(e.target).get('name')});$('#modal').close();render();}catch(error){toast(error.message);}};}
    if(d.deletePlaylist){modal('<h2>Delete this playlist?</h2><p>Your songs and downloads will stay in your library.</p><button class="primary" data-confirm-delete="'+d.deletePlaylist+'">Delete playlist</button>');}
    if(d.confirmDelete){state.playlists=await api('playlists',{action:'delete',id:Number(d.confirmDelete)});$('#modal').close();state.view='home';render();}
    if(d.export){const p=state.playlists.find(p=>p.id===Number(d.export));const rows=['#EXTM3U',...p.tracks.filter(t=>t.hasFile).flatMap(t=>['#EXTINF:'+Math.round(t.duration/1000)+','+t.artist+' - '+t.title,location.origin+'/api/stream/'+t.id])];const link=document.createElement('a');link.href=URL.createObjectURL(new Blob([rows.join('\n')],{type:'audio/x-mpegurl'}));link.download=p.name.replace(/[^a-z0-9_-]/gi,'_')+'.m3u';link.click();setTimeout(()=>URL.revokeObjectURL(link.href),1000);toast('Exported. Playback URLs require a signed-in browser session.');}
    if(d.activity!==undefined){$('#activity').textContent='Checking…';const a=await api('activity');const queue=a.queue.records||[];$('#activity').innerHTML=queue.length||a.soulseek.length?queue.map(q=>`<div class="artist-result"><div><strong>${escape(q.title)}</strong><small>${escape(q.status)} · ${escape(q.trackedDownloadStatus||'')} ${escape(q.statusMessages?.flatMap(m=>m.messages||[]).join(' · ')||'')}</small></div></div>`).join('')+a.soulseek.map(q=>`<div class="artist-result"><div><strong>Soulseek · Track ${escape(q.trackId)}</strong><small>${escape(q.status)} ${escape(q.error||'')}</small></div></div>`).join(''):'<p class="page-subtitle">Nothing downloading right now.</p>';}
    if(d.addArtist){b.disabled=true;const r=await api('artists',{foreignArtistId:d.addArtist});toast(r.message);b.textContent='Added';}
  }catch(error){toast(error.message);b.disabled=false;}
}
document.addEventListener('click',action);
document.addEventListener('error',e=>{if(e.target.tagName==='IMG')e.target.hidden=true;},true);
$('#login-form').onsubmit=async e=>{e.preventDefault();const button=e.target.querySelector('button');button.disabled=true;$('#login-error').textContent='';try{await start(await api('login',Object.fromEntries(new FormData(e.target))));e.target.reset();}catch(error){$('#login-error').textContent=error.message;}finally{button.disabled=false;}};
$('#logout').onclick=async()=>{try{await api('logout',{});showLogin();}catch(error){toast(error.message);}};
$('#new-playlist').onclick=()=>createPlaylist();
$('#modal-close').onclick=()=>$('#modal').close();
$('#refresh').onclick=async()=>{const b=$('#refresh');b.disabled=true;await load(true);b.disabled=false;toast(state.error||'Catalog refreshed.');};
$('#search').oninput=e=>{state.query=e.target.value;clearTimeout(searchTimer);searchTimer=setTimeout(render,150);};
document.addEventListener('keydown',e=>{if(e.key==='/'&&!['INPUT','TEXTAREA'].includes(document.activeElement.tagName)){e.preventDefault();$('#search').focus();}});
document.addEventListener('submit',async e=>{
  if(e.target.id==='artist-search'){e.preventDefault();$('#artist-results').textContent='Searching artists…';try{const q=new FormData(e.target).get('q');const results=await api('artists?q='+encodeURIComponent(q));$('#artist-results').innerHTML=results.map(a=>`<div class="artist-result"><div><strong>${escape(a.artistName)}</strong><small>${escape(a.disambiguation||a.artistType||'Artist')}</small></div><button class="secondary" data-add-artist="${escape(a.foreignArtistId)}" ${state.artists.some(x=>x.name===a.artistName)?'disabled':''}>Add artist ＋</button></div>`).join('')||'<p class="page-subtitle">No artists found.</p>';}catch(error){$('#artist-results').textContent=error.message;}}
  if(e.target.id==='user-create'){e.preventDefault();try{await api('users',Object.fromEntries(new FormData(e.target)));e.target.reset();toast('Listener account created.');}catch(error){toast(error.message);}}
});
document.addEventListener('dragstart',e=>{const row=e.target.closest('.drag-row');if(row){dragged=row.dataset.identity;row.classList.add('dragging');e.dataTransfer.setData('text/plain',dragged);}});
document.addEventListener('dragend',e=>e.target.closest('.drag-row')?.classList.remove('dragging'));
document.addEventListener('dragover',e=>{if(e.target.closest('.drag-row'))e.preventDefault();});
document.addEventListener('drop',async e=>{const row=e.target.closest('.drag-row');if(!row||!dragged)return;e.preventDefault();const p=state.playlists.find(p=>p.id===Number(state.view.split(':')[1]));if(!p)return;const keys=p.tracks.map(t=>t.identity);const from=keys.indexOf(dragged),to=keys.indexOf(row.dataset.identity);if(from<0||to<0)return;keys.splice(from,1);keys.splice(to,0,dragged);try{state.playlists=await api('playlists',{action:'reorder',id:p.id,identities:keys});render();}catch(error){toast(error.message);}});
$('#toggle-play').onclick=()=>{if(!audio.src){const t=tracksForView().find(t=>t.hasFile);if(t)play(t.id);else toast('No downloaded songs in this view yet.');}else if(audio.paused)audio.play().catch(()=>toast('Playback is unavailable.'));else audio.pause();};
$('#previous').onclick=()=>skip(-1);$('#next').onclick=()=>skip();
$('#shuffle').onclick=()=>{state.shuffle=!state.shuffle;$('#shuffle').classList.toggle('on',state.shuffle);};
$('#repeat').onclick=()=>{state.repeat=!state.repeat;$('#repeat').classList.toggle('on',state.repeat);};
audio.onplay=()=>$('#toggle-play').textContent='Ⅱ';audio.onpause=()=>$('#toggle-play').textContent='▶';audio.onended=()=>skip();
audio.onerror=()=>toast('Unable to play this file. Check the shared music mount; some audio formats need an external player.');
audio.ontimeupdate=()=>{$('#elapsed').textContent=time(audio.currentTime);$('#duration').textContent=time(audio.duration);$('#seek').value=audio.duration?audio.currentTime/audio.duration*100:0;};
$('#seek').oninput=e=>{if(Number.isFinite(audio.duration))audio.currentTime=audio.duration*e.target.value/100;};
$('#volume').oninput=e=>audio.volume=Number(e.target.value);
$('#queue-toggle').onclick=()=>modal('<h2>Up next.</h2><p>Your current playback queue.</p>'+table(state.queue));
(async()=>{try{await start(await api('me'));}catch(_){showLogin();}})();
