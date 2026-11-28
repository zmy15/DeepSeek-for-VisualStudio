
document.addEventListener('keydown',function(e){
    var isF5=e.key==='F5'||e.code==='F5';
    var isF7=e.key==='F7'||e.code==='F7';
    if(!isF5&&!isF7)return;

    e.preventDefault();
    e.stopPropagation();
    if(e.repeat)return;

    if(isF5){
        var plain=!e.ctrlKey&&!e.altKey&&!e.shiftKey&&!e.metaKey;
        if(!plain)return;
        try{
            if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
                window.chrome.webview.postMessage(JSON.stringify({type:'debugShortcut'}));
            }
        }catch(_){}
        return;
    }

    if(e.ctrlKey||e.altKey||e.metaKey)return;
    try{
        if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
            window.chrome.webview.postMessage(JSON.stringify({type:'viewCodeShortcut',designer:e.shiftKey}));
        }
    }catch(_){}
},true);
