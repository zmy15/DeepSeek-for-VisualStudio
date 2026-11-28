
(function(){
    var MAP={'✅':'E73E','✔':'E73E','❌':'E711','⚠️':'E7BA','⚠':'E7BA','ℹ️':'E946',
             '🔍':'E721','🔎':'E721','📝':'E70F','📋':'E8C8','💡':'EA80','📁':'E8B7',
             '📂':'E8B7','📦':'E8B7','💻':'E756','🌐':'E774','🔄':'E72C','⏳':'E81C',
             '⏱️':'E81C','🚀':'E945','🧠':'E81C','📄':'E8A5','📎':'E723','🗑️':'E74D',
             '🎯':'E945','📊':'E9D2','🔧':'E90F','⚙️':'E713','🔌':'E713','💬':'E8BD',
             '📷':'E722','🖼️':'E7C3','⚡':'E945'};
    var RE=/[\uD83C-\uDBFF][\uDC00-\uDFFF]|[\u2139\u2300-\u23FF\u2460-\u24FF\u2600-\u27BF\u2B00-\u2BFF\u2900-\u297F]\uFE0F?|[\u2190-\u21FF\u25A0-\u25FF]\uFE0F/g;
    function detoxText(node){
        var text=node.nodeValue;
        RE.lastIndex=0;
        if(!RE.test(text))return;
        var frag=document.createDocumentFragment();
        var last=0,m;
        RE.lastIndex=0;
        while((m=RE.exec(text))!==null){
            if(m.index>last)frag.appendChild(document.createTextNode(text.slice(last,m.index)));
            var glyph=MAP[m[0]];
            if(glyph){
                var sp=document.createElement('span');
                sp.className='icf';
                sp.textContent=String.fromCodePoint(parseInt(glyph,16));
                frag.appendChild(sp);
            }
            last=m.index+m[0].length;
        }
        if(last<text.length)frag.appendChild(document.createTextNode(text.slice(last)));
        node.parentNode.replaceChild(frag,node);
    }
    function detox(root){
        if(!root)return;
        var w=document.createTreeWalker(root,NodeFilter.SHOW_TEXT,null,false);
        var targets=[];
        while(w.nextNode()){
            var p=w.currentNode.parentNode;
            if(p&&p.closest&&p.closest('pre,code,.icf,.cache-stat-card,.msg-bubble.user'))continue;
            targets.push(w.currentNode);
        }
        for(var i=0;i<targets.length;i++)detoxText(targets[i]);
    }
    function scheduleDetox(el){
        if(!el)return;
        if(el.__detoxPending)return;
        el.__detoxPending=true;
        requestAnimationFrame(function(){el.__detoxPending=false;detox(el);});
    }
    document.addEventListener('DOMContentLoaded',function(){
        var cc=document.getElementById('chat-container');
        scheduleDetox(cc);
        if(cc&&window.MutationObserver){
            new MutationObserver(function(muts){
                for(var i=0;i<muts.length;i++){
                    var added=muts[i].addedNodes;
                    for(var j=0;j<added.length;j++)
                        if(added[j].nodeType===1)scheduleDetox(added[j]);
                }
            }).observe(cc,{childList:true,subtree:true});
        }
    });
})();
