
(function(){
var _autoScroll=true;
window._autoScroll=true;   // 暴露为全局，供其他 JS 模块读取
var SCROLL_THRESHOLD=80;
var timer=null;

// 定义全局智能滚动函数（所有 scrollToBottom 通过此函数）
window.__scrollToBottom=function(behavior){
    if(window._autoScroll===false)return;
    window.scrollTo({top:document.body.scrollHeight,behavior:behavior||'smooth'});
};

// 思考面板自身也有滚动条，需要独立跟随最新内容
window.__isReasoningAtBottom=function(el){
    if(!el)return false;
    return el.scrollHeight-el.scrollTop-el.clientHeight<=32;
};
window.__scrollReasoningToBottom=function(el){
    if(!el)return;
    el.scrollTop=el.scrollHeight;
};

// 检测用户手动滚动
window.addEventListener('scroll',function(){
    var atBottom=(window.innerHeight+window.scrollY+SCROLL_THRESHOLD)>=document.body.scrollHeight;
    if(!atBottom&&_autoScroll){ _autoScroll=false; window._autoScroll=false; }
    else if(atBottom&&!_autoScroll){ _autoScroll=true; window._autoScroll=true; }
},{passive:true});

// 内容变更时若在底部则自动滚动（范围缩小到 #chat-container，防抖 120ms）
var chatContainer=document.getElementById('chat-container')||document.body;
new MutationObserver(function(){
    if(!_autoScroll)return;
    if(timer)clearTimeout(timer);
    timer=setTimeout(function(){ window.__scrollToBottom('smooth'); },120);
}).observe(chatContainer,{childList:true,subtree:true});
})();
