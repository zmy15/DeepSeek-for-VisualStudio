var __diagramClickTip="{{diagramClickTip}}";
window.__openDiagram=function(pre){
    try{
        var svg=pre.querySelector('svg');
        if(!svg)return;
        // 导出前去掉写死的尺寸，避免放大后仍被裁切
        var clone=svg.cloneNode(true);
        clone.removeAttribute('width');
        clone.removeAttribute('height');
        clone.style.maxWidth='none';
        clone.style.height='auto';
        // 独立 .svg 文件按 XML 解析：必须用 XML 序列化，
        // 否则 foreignObject 里的 <br> 等 HTML 空元素会写成非法闭合
        var markup;
        try{
            markup=new XMLSerializer().serializeToString(clone);
        }catch(serErr){
            markup=clone.outerHTML;
        }
        window.__sendToHost({
            type:'saveDiagramToFile',
            svg:markup
        });
    }catch(e){
        try{console.error('[DeepSeek] openDiagram:',e);}catch(e2){}
    }
};
window.__renderMermaid=function(container){
    if(!container)return;
    if(typeof mermaid==='undefined'){
        if(!window.__mermaidRetryCount)window.__mermaidRetryCount=0;
        if(window.__mermaidRetryCount<20){
            window.__mermaidRetryCount++;
            setTimeout(function(){window.__renderMermaid(container);},500);
        }
        return;
    }
    try{
        var codes=container.querySelectorAll('code.language-mermaid');
        for(let i=0;i<codes.length;i++){
            let code=codes[i];
            let pre=code.parentElement;
            if(!pre||pre.classList.contains('mermaid-block'))continue;
            let src=code.textContent.trim();
            if(!src)continue;
            let id='mermaid-svg-'+(Date.now())+'-'+i;
            // 先用 parse() 预校验语法，语法错误时不调用 render()，从根源避免 Mermaid 注入错误 UI
            (async function(){
                try{
                    await mermaid.parse(src);
                    // 解析通过，开始渲染
                    var result=await mermaid.render(id,src);
                    pre.innerHTML=result.svg;
                    pre.classList.add('mermaid-block');
                    // 渲染成功：整块图可点击导出，并给出可发现性提示
                    pre.setAttribute('title',__diagramClickTip);
                    pre.style.cursor='zoom-in';
                    pre.onclick=function(){window.__openDiagram(pre);};
                }catch(e){
                    // 语法错误或渲染失败时保留源码，红色左边框标记
                    pre.classList.add('mermaid-block');
                    pre.style.borderLeft='3px solid #e07878';
                }
            })();
        }
    // 清理 Mermaid 可能注入到 body/container 的错误元素（炸弹图标等）
    setTimeout(function(){
        var allSvgs=document.querySelectorAll('svg[id]');
        for(var s=0;s<allSvgs.length;s++){
            var txt=(allSvgs[s].textContent||'').toLowerCase();
            if(txt.indexOf('syntax error')>=0||txt.indexOf('syntaxerror')>=0){
                allSvgs[s].parentElement&&allSvgs[s].parentElement.removeChild(allSvgs[s]);
            }
        }
    },200);
    }catch(e){}
};