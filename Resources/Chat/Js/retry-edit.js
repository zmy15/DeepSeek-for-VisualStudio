var __markdownFailText="{{markdownFailText}}";var __actLabels={retry:"{{retry}}",retryTitle:"{{retryTitle}}",copy:"{{copy}}",copyTitle:"{{copyTitle}}",copied:"{{copied}}"};
window.__sendToHost=function(msg){
    try{window.chrome.webview.postMessage(JSON.stringify(msg));}
    catch(e1){try{window.external.notify(JSON.stringify(msg));}catch(e2){}}
};
window.__openAttachment=function(path){
    if(!path)return;
    window.__sendToHost({type:'openAttachment',path:path});
};
window.__saveSvgImage=function(element){
    try{
        var container=element&&element.closest?element.closest('.svg-diagram-preview'):null;
        var img=container?container.querySelector('img'):null;
        var src=img?(img.getAttribute('src')||''):'';
        var comma=src.indexOf(',');
        if(comma<0)return;
        var binary=atob(src.substring(comma+1));
        var bytes=new Uint8Array(binary.length);
        for(var i=0;i<binary.length;i++)bytes[i]=binary.charCodeAt(i);
        var svg;
        if(typeof TextDecoder!=='undefined'){
            svg=new TextDecoder('utf-8').decode(bytes);
        }else{
            var escaped='';
            for(var j=0;j<bytes.length;j++)escaped+='%'+bytes[j].toString(16).padStart(2,'0');
            svg=decodeURIComponent(escaped);
        }
        window.__sendToHost({type:'saveDiagramAs',svg:svg});
    }catch(e){
        try{console.error('[DeepSeek] saveSvgImage:',e);}catch(e2){}
    }
};
window.__closeSvgPreview=function(){
    var overlay=document.getElementById('svg-preview-overlay');
    if(overlay){
        overlay._svgZoom=1;
        overlay._svgPanX=0;
        overlay._svgPanY=0;
        overlay._svgOverlayPointerDown=false;
        overlay.classList.remove('is-open','is-zoomed','is-dragging');
        window.__applySvgPreviewTransform(overlay);
    }
    document.body.classList.remove('svg-preview-open');
};
window.__applySvgPreviewTransform=function(overlay){
    if(!overlay)return;
    var img=overlay.querySelector('.svg-preview-image-container img');
    var zoom=overlay._svgZoom||1;
    var panX=overlay._svgPanX||0;
    var panY=overlay._svgPanY||0;
    if(img)img.style.transform='translate('+panX+'px,'+panY+'px) scale('+zoom+')';
    var badge=overlay.querySelector('.svg-preview-zoom');
    if(badge)badge.textContent=Math.round(zoom*100)+'%';
};
window.__openSvgPreview=function(img){
    try{
        if(!img)return;
        var overlay=document.getElementById('svg-preview-overlay');
        if(!overlay){
            overlay=document.createElement('div');
            overlay.id='svg-preview-overlay';
            overlay.className='svg-preview-overlay';
            var canvas=document.createElement('div');
            canvas.className='svg-preview-image-container';
            var close=document.createElement('button');
            close.type='button';
            close.className='svg-preview-close';
            close.textContent='×';
            close.addEventListener('click',function(){window.__closeSvgPreview();});
            var zoomLabel=document.createElement('div');
            zoomLabel.className='svg-preview-zoom';
            zoomLabel.textContent='100%';
            overlay.appendChild(canvas);
            overlay.appendChild(close);
            overlay.appendChild(zoomLabel);
            overlay.addEventListener('click',function(e){
                if(e.target===overlay&&overlay._svgOverlayPointerDown)window.__closeSvgPreview();
                overlay._svgOverlayPointerDown=false;
            });
            overlay.addEventListener('wheel',function(e){
                if(!overlay.classList.contains('is-open'))return;
                e.preventDefault();
                var factor=e.deltaY<0?1.15:1/1.15;
                var next=Math.max(1,Math.min(8,(overlay._svgZoom||1)*factor));
                overlay._svgZoom=next;
                if(next<=1){
                    overlay._svgPanX=0;
                    overlay._svgPanY=0;
                }
                overlay.classList.toggle('is-zoomed',next>1);
                window.__applySvgPreviewTransform(overlay);
            },{passive:false});
            overlay.addEventListener('pointerdown',function(e){
                overlay._svgOverlayPointerDown=e.target===overlay;
                if(e.target.tagName!=='IMG'||(overlay._svgZoom||1)<=1)return;
                e.preventDefault();
                overlay._svgDragging=true;
                overlay._svgDragX=e.clientX;
                overlay._svgDragY=e.clientY;
                overlay.classList.add('is-dragging');
                try{overlay.setPointerCapture(e.pointerId);}catch(e2){}
            });
            overlay.addEventListener('pointermove',function(e){
                if(!overlay._svgDragging)return;
                overlay._svgPanX=(overlay._svgPanX||0)+(e.clientX-(overlay._svgDragX||0));
                overlay._svgPanY=(overlay._svgPanY||0)+(e.clientY-(overlay._svgDragY||0));
                overlay._svgDragX=e.clientX;
                overlay._svgDragY=e.clientY;
                window.__applySvgPreviewTransform(overlay);
            });
            var stopSvgDrag=function(e){
                if(!overlay._svgDragging)return;
                overlay._svgDragging=false;
                overlay.classList.remove('is-dragging');
                try{overlay.releasePointerCapture(e.pointerId);}catch(e2){}
            };
            overlay.addEventListener('pointerup',stopSvgDrag);
            overlay.addEventListener('pointercancel',stopSvgDrag);
            document.body.appendChild(overlay);
        }
        var imageContainer=overlay.querySelector('.svg-preview-image-container');
        var closeButton=overlay.querySelector('.svg-preview-close');
        var closeLabel=img.getAttribute('data-preview-close-label')||'Close';
        if(closeButton){
            closeButton.setAttribute('aria-label',closeLabel);
            closeButton.title=closeLabel;
        }
        if(imageContainer){
            imageContainer.textContent='';
            var enlarged=document.createElement('img');
            enlarged.src=img.currentSrc||img.src;
            enlarged.alt=img.alt||'SVG diagram';
            imageContainer.appendChild(enlarged);
        }
        overlay._svgZoom=1;
        overlay._svgPanX=0;
        overlay._svgPanY=0;
        overlay._svgDragging=false;
        overlay._svgOverlayPointerDown=false;
        overlay.classList.remove('is-zoomed','is-dragging');
        window.__applySvgPreviewTransform(overlay);
        overlay.classList.add('is-open');
        document.body.classList.add('svg-preview-open');
    }catch(e){
        try{console.error('[DeepSeek] openSvgPreview:',e);}catch(e2){}
    }
};
window.__wireSvgDiagramPreviews=function(container){
    if(!container)return;
    var previews=container.querySelectorAll('.svg-diagram-preview');
    for(var i=0;i<previews.length;i++){
        var preview=previews[i];
        if(preview.getAttribute('data-svg-wired')==='1')continue;
        var img=preview.querySelector('img.svg-preview-target');
        var button=preview.querySelector('button.svg-save-button');
        if(img){
            img.addEventListener('click',function(){window.__openSvgPreview(this);});
            img.addEventListener('keydown',function(e){
                if(e.key==='Enter'||e.key===' '){
                    e.preventDefault();
                    window.__openSvgPreview(this);
                }
            });
        }
        if(button)button.addEventListener('click',function(){window.__saveSvgImage(this);});
        preview.setAttribute('data-svg-wired','1');
    }
};
document.addEventListener('keydown',function(e){
    if(e.key==='Escape'||e.key==='Esc')window.__closeSvgPreview();
});
window.__retryMessage=function(msgIndex){
    window.__sendToHost({type:'retryMessage',messageIndex:msgIndex});
};
window.__editMessage=function(msgIndex){
    window.__sendToHost({type:'editMessage',messageIndex:msgIndex});
};
window.__editMessageConfirm=function(msgIndex,newText){
    window.__sendToHost({type:'editMessageConfirm',messageIndex:msgIndex,text:newText});
};
window.__loadEarlier=function(){
    window.__sendToHost({type:'loadEarlier'});
};
window.__editMessageCancel=function(msgIndex){
    window.__sendToHost({type:'editMessageCancel',messageIndex:msgIndex});
};
window.__navigateVersion=function(msgIndex,direction){
    window.__sendToHost({type:'navigateVersion',messageIndex:msgIndex,direction:direction});
};
window.__navigateBranch=function(nodeId,direction){
    window.__sendToHost({type:'navigateBranch',nodeId:nodeId,direction:direction});
};
window.__agentApprove=function(requestId){
    window.__sendToHost({type:'agentApprove',requestId:requestId,approved:true});
};
window.__agentDeny=function(requestId){
    window.__sendToHost({type:'agentApprove',requestId:requestId,approved:false});
};
window.__fileDeleteConfirm=function(requestId){
    window.__sendToHost({type:'fileDeleteConfirm',requestId:requestId,confirmed:true});
};
window.__fileDeleteCancel=function(requestId){
    window.__sendToHost({type:'fileDeleteConfirm',requestId:requestId,confirmed:false});
};
window.__terminalApprove=function(requestId){
    window.__sendToHost({type:'terminalApprove',requestId:requestId});
};
window.__terminalSkip=function(requestId){
    window.__sendToHost({type:'terminalSkip',requestId:requestId});
};
window.__answerQuestions=function(requestId){
    // 分页模式下所有题目都在 DOM 中（仅显隐切换），因此提交时按页顺序统一收集答案
    var answers=[];
    var card=document.getElementById('agent-questions');
    if(card){
        var pages=card.querySelectorAll('.aq-page');
        for(var i=0;i<pages.length;i++){
            var page=pages[i];
            var inputs=page.querySelectorAll('input[type=radio]:checked,input[type=checkbox]:checked');
            var selected=[];
            for(var j=0;j<inputs.length;j++) selected.push(inputs[j].value);
            var freeText='';
            var textarea=page.querySelector('textarea');
            if(textarea) freeText=textarea.value.trim();
            var skipped=page.getAttribute('data-skipped')==='true';
            answers.push({selectedOptions:selected,freeText:freeText,skipped:skipped});
        }
    }
    var answersJson=JSON.stringify(answers);
    // 先移除 UI
    var el=document.getElementById('agent-questions');
    if(el) el.remove();
    window.__sendToHost({type:'answerQuestions',requestId:requestId,answers:answersJson});
};
window.__askQuestionsNav=function(requestId,delta){
    var card=document.getElementById('agent-questions');
    if(!card)return;
    var pages=card.querySelectorAll('.aq-page');
    if(!pages.length)return;
    var current=parseInt(card.getAttribute('data-index')||'0',10);
    if(isNaN(current))current=0;
    var target=current+delta;
    if(target<0)target=0;
    if(target>pages.length-1)target=pages.length-1;
    window.__askQuestionsShowPage(card,target);
};
window.__askQuestionsShowPage=function(card,index){
    if(!card)return;
    var pages=card.querySelectorAll('.aq-page');
    if(!pages.length)return;
    if(index<0)index=0;
    if(index>pages.length-1)index=pages.length-1;

    for(var i=0;i<pages.length;i++){
        pages[i].style.display=(i===index)?'block':'none';
    }
    card.setAttribute('data-index',String(index));

    var isLast=(index===pages.length-1);

    // 进度：第 N / M 题
    var progress=document.getElementById('agent-questions-progress');
    if(progress){
        var template=progress.getAttribute('data-template')||'{0} / {1}';
        progress.textContent=template.replace('{0}',String(index+1)).replace('{1}',String(pages.length));
    }

    // 切换箭头：首页隐藏「上一题」，末页隐藏「下一题」（末页改用提交按钮）
    var prev=document.getElementById('agent-questions-prev');
    if(prev)prev.style.visibility=(index===0)?'hidden':'visible';
    var next=document.getElementById('agent-questions-next');
    if(next)next.style.display=isLast?'none':'inline-block';

    // 提交按钮只在最后一题显示
    var submit=document.getElementById('agent-questions-submit');
    if(submit)submit.style.display=isLast?'inline-block':'none';

    // 等待用户回答期间不存在模型流式输出，清理静态重建留下的光标。
    var streamCursors=document.querySelectorAll('.streaming-cursor');
    for(var si=0;si<streamCursors.length;si++)streamCursors[si].style.display='none';
};
window.__skipCurrentQuestion=function(requestId){
    var card=document.getElementById('agent-questions');
    if(!card)return;
    var pages=card.querySelectorAll('.aq-page');
    if(!pages.length)return;
    var current=parseInt(card.getAttribute('data-index')||'0',10);
    if(isNaN(current))current=0;
    if(current<0)current=0;
    if(current>pages.length-1)current=pages.length-1;

    var page=pages[current];
    page.setAttribute('data-skipped','true');
    var inputs=page.querySelectorAll('input[type=radio],input[type=checkbox]');
    for(var i=0;i<inputs.length;i++)inputs[i].checked=false;
    var textarea=page.querySelector('textarea');
    if(textarea)textarea.value='';

    if(current<pages.length-1){
        window.__askQuestionsShowPage(card,current+1);
        return;
    }
    window.__answerQuestions(requestId);
};
window.__closeQuestions=function(requestId){
    var el=document.getElementById('agent-questions');
    if(el) el.remove();
    window.__sendToHost({type:'answerQuestions',requestId:requestId,answers:'[]'});
};
window.__executeHandoff=function(targetAgent,label){
    window.__sendToHost({type:'executeHandoff',targetAgent:targetAgent,label:label});
};
window.__copyMessage=function(msgIndex){
    var container=document.getElementById('msg-body-'+msgIndex);
    if(!container)return;
    // 优先读取原始 Markdown 内容（`data-raw-content`），避免复制渲染后的 HTML 文本
    var text=container.getAttribute('data-raw-content')||container.innerText||container.textContent||'';
    var ok=false;
    if(navigator.clipboard&&navigator.clipboard.writeText){
        navigator.clipboard.writeText(text).then(function(){window._showCopyFeedback(msgIndex);});
        return;
    }else{
        var ta=document.createElement('textarea');
        ta.value=text;ta.style.cssText='position:fixed;opacity:0';
        document.body.appendChild(ta);ta.select();
        try{document.execCommand('copy');ok=true;}catch(e){}
        document.body.removeChild(ta);
    }
    if(ok)window._showCopyFeedback(msgIndex);
};
window._showCopyFeedback=function(msgIndex){
    var btn=document.getElementById('copy-btn-'+msgIndex);
    if(!btn)return;
    var copyLabel=btn.getAttribute('data-copy-label')||(__actLabels&&__actLabels.copy)||'';
    var copiedLabel=btn.getAttribute('data-copied-label')||(__actLabels&&__actLabels.copied)||'';
    btn.classList.add('copied');
    btn.textContent=copiedLabel;
    btn.style.fontFamily='';
    btn.style.position='';
    setTimeout(function(){
        btn.classList.remove('copied');
        btn.textContent=copyLabel;btn.style.fontFamily='';
        btn.style.position='';
    },1200);
};

// ═══════════════════════════════════════════════
//  流式更新批处理引擎（性能优化核心）
//  C# 通过 PostWebMessageAsString 推送 JSON，
//  JS 通过 requestAnimationFrame 批量应用 DOM 更新，
//  避免每次 ExecuteScriptAsync 阻塞 UI 线程。
// ═══════════════════════════════════════════════
(function(){
    var _streamBuf={};          // 消息索引 → 累积内容
    var _htmlChunks={};         // 大段初始 HTML 的分块投递缓冲区
    var _rafPending=false;      // 是否已有待处理的 rAF
    var _rafTimer=null;         // 兜底定时器（防止 rAF 不触发）

    // ═══ Context Debugger 面板（P2，序号 20 渲染层）═══
    // 懒创建：首条 contextDebug 消息到达时注入固定抽屉（右上角，默认折叠）
    function _applyCtxDebugTheme(panel){
        if(!panel)return;
        var light=document.documentElement.getAttribute('data-theme')==='light';
        var colors=light?{
            bg:'#ffffffee',text:'#333333',border:'#0078d4',
            head:'#005a9e',divider:'#d0d7de',shadow:'rgba(0,0,0,.16)'
        }:{
            bg:'#252526ee',text:'#d4d4d4',border:'#007acc',
            head:'#9cdcfe',divider:'#3f3f46',shadow:'rgba(0,0,0,.4)'
        };
        panel.style.background=colors.bg;
        panel.style.borderColor=colors.border;
        panel.style.color=colors.text;
        panel.style.boxShadow='0 4px 14px '+colors.shadow;
        var head=document.getElementById('ctx-debug-head');
        var body=document.getElementById('ctx-debug-body');
        if(head)head.style.color=colors.head;
        if(body)body.style.borderTopColor=colors.divider;
    }
    window.__applyCtxDebugTheme=function(){
        _applyCtxDebugTheme(document.getElementById('ctx-debug'));
    };
    function _ensureCtxPanel(){
        var p=document.getElementById('ctx-debug');
        if(p){
            _applyCtxDebugTheme(p);
            return p;
        }
        p=document.createElement('div');
        p.id='ctx-debug';
        p.style.cssText='position:fixed;top:8px;right:12px;z-index:9999;'
            +'border:1px solid #007acc;border-radius:8px;font:11px/1.55 Consolas,monospace;'
            +'max-width:420px;display:none;';
        var head=document.createElement('div');
        head.id='ctx-debug-head';
        head.textContent=(window.__ctxDebugLabels||{}).title||'Context';
        head.style.cssText='padding:4px 10px;cursor:pointer;user-select:none;';
        var body=document.createElement('div');
        body.id='ctx-debug-body';
        body.style.cssText='display:none;padding:6px 10px 8px;border-top:1px solid #3f3f46;white-space:pre-wrap;';
        head.addEventListener('click',function(){
            var open=body.style.display!=='none';
            body.style.display=open?'none':'block';
            head.textContent=(window.__ctxDebugLabels||{}).title||'Context';
        });
        p.appendChild(head);p.appendChild(body);
        document.body.appendChild(p);
        _applyCtxDebugTheme(p);
        return p;
    }
    function _renderCtxDebug(d){
        try{
            var panel=_ensureCtxPanel();
            panel.style.display='block';
            _applyCtxDebugTheme(panel);
            var t=window.__ctxDebugLabels||{};
            var rows=[];
            var tk=d.tokens||{};
            rows.push((t.tokens||'Tokens')+' : '+(tk.estimated||0).toLocaleString()+' / '+(tk.budget||0).toLocaleString()
                +'  ('+(tk.percent!=null?tk.percent:0)+'%)');
            var inj=d.injected||{};
            rows.push((t.injected||'Injected')+' : IDE='+!!inj.ide+' ('+(inj.ideChars||0)+' '+(t.characters||'ch')+')'
                +'  IDE='+!!inj.ide);
            var ide=d.ideSnapshot;
            if(ide){
                rows.push((t.file||'File')+' : '+(ide.file||'-'));
                if(ide.hasSelection)rows.push((t.selection||'Selection')+' : L'+ide.selectionStartLine+'-L'+ide.selectionEndLine);
                rows.push((t.cursor||'Cursor')+' : L'+(ide.cursorLine||0)+(ide.symbol?('   '+(t.symbol||'Symbol')+': '+ide.symbol):''));
                rows.push((t.diagnostics||'Diags')+' : '+(ide.errors||0)+' '+(t.errors||'err')+' / '+(ide.warnings||0)+' '+(t.warnings||'warn'));
            }else{
                rows.push('IDE : '+(t.off||'(off)'));
            }
            rows.push((t.turns||'Turns')+' : '+(d.turns||0)
                +'    '+(t.messages||'Messages')+' : '+(d.messages||0)
                +'    '+(t.toolCalls||'Tool Calls')+' : '+(d.toolCalls||0));
            if(d.forwarded){
                rows.push((t.forwarded||'Forwarded')+' : '+(t.messages||'Messages')+' '+(d.forwarded.messages||0)
                    +'    '+(t.toolCalls||'Tool Calls')+' '+(d.forwarded.toolCalls||0)
                    +'    ~'+((d.forwarded.estimatedTokens||0)/1000).toFixed(1)+'k '+(t.tokens||'Tokens'));
            }
            if(d.forwarded){
                var totalMessages=(d.messages||0)+(d.forwarded.messages||0);
                var totalToolCalls=(d.toolCalls||0)+(d.forwarded.toolCalls||0);
                var totalTokens=(tk.estimated||0)+(d.forwarded.estimatedTokens||0);
                rows.push((t.requestEstimate||'Request Estimate')+' : '+(t.messages||'Messages')+' '+totalMessages.toLocaleString()
                    +'    '+(t.toolCalls||'Tool Calls')+' '+totalToolCalls.toLocaleString()
                    +'    ~'+(totalTokens/1000).toFixed(1)+'k '+(t.tokens||'Tokens'));
            }
            if(d.tokensPerSecond){
                var speed=d.tokensPerSecond;
                var speedParts=[];
                if(speed.decode!==null&&speed.decode!==undefined)speedParts.push('decode '+Number(speed.decode).toFixed(1));
                if(speed.endToEnd!==null&&speed.endToEnd!==undefined)speedParts.push('end-to-end '+Number(speed.endToEnd).toFixed(1));
                rows.push((t.tokensPerSecond||'Tokens/s')+' : '+(speed.turn?'turn '+speed.turn+' | ':'')
                    +(speed.outputTokens||0)+' '+(t.tokens||'Tokens')+' | '+speedParts.join(' | '));
            }
            var body=document.getElementById('ctx-debug-body');
            if(body)body.textContent=rows.join('\n');
        }catch(err){ console.error('[DeepSeek] ctxDebug render:',err); }
    }

    // 监听来自 C# 的流式更新消息
    if(window.chrome&&window.chrome.webview){
        window.chrome.webview.addEventListener('message',function(e){
            try{
                var msg=typeof e.data==='string'?JSON.parse(e.data):e.data;
                if(msg.type==='contextDebug'){
                    _renderCtxDebug(msg.d||{});
                    return;
                }
                if(msg.type==='htmlChunk'){
                    var chunkBuf=_htmlChunks[msg.id];
                    if(msg.reset||!chunkBuf){
                        chunkBuf=[];
                        _htmlChunks[msg.id]=chunkBuf;
                    }
                    if(msg.html)chunkBuf.push(msg.html);
                    if(msg.final){
                        delete _htmlChunks[msg.id];
                        window.__appendMessageHtml(chunkBuf.join(''));
                    }
                    return;
                }
                if(msg.type==='stream'){
                    _streamBuf[msg.i]=msg;
                    if(!_rafPending){
                        _rafPending=true;
                        requestAnimationFrame(_flushStreamBuf);
                        // 兜底：250ms 后强制执行（防止 rAF 在某些场景不触发）
                        if(_rafTimer)clearTimeout(_rafTimer);
                        _rafTimer=setTimeout(function(){
                            if(_rafPending)_flushStreamBuf(0);
                        },250);
                    }
                }else if(msg.type==='streamEnd'){
                    // 先取消待处理的 rAF 和定时器，防止 _flushStreamBuf 覆盖已渲染的 Markdown
                    if(_rafTimer){clearTimeout(_rafTimer);_rafTimer=null;}
                    _rafPending=false;
                    delete _streamBuf[msg.i];  // 清除可能残留的流式缓冲，避免 textNode 覆盖 innerHTML

                    // ── 兜底：无论渲染成功与否，先清理流式状态（光标、textNode）──
                    var cursorEl = document.getElementById('cursor-' + msg.i);
                    if (cursorEl) cursorEl.style.display = 'none';
                    var containerEl = document.getElementById('msg-body-' + msg.i);
                    if (containerEl) {
                        containerEl._textNode = null;
                        containerEl.style.whiteSpace = '';
                    }
                    var finalizedReasoningBody = document.getElementById('reasoning-body-' + msg.i);
                    if (finalizedReasoningBody) {
                        finalizedReasoningBody._streamComplete = true;
                        finalizedReasoningBody._textNode = null;
                    }

                    // 渲染 Markdown（独立于 _flushStreamBuf，不受其异常影响）
                    if (msg.html) {
                        try {
                            var container = containerEl;
                            if (container) {
                                container.innerHTML = msg.html;
                                // 存储原始 Markdown 内容，供复制按钮使用
                                if (msg.rawContent) {
                                    container.setAttribute('data-raw-content', msg.rawContent);
                                }
                                // 注入尾部 HTML：容器必须与 C# 侧 BuildFooterContainerHtml 一致
                                // （class=msg-footer，且落在 #msg-body-N 内部），否则页面重建后
                                // 缓存条会从内容卡片里掉出来变成独立卡片。
                                if(msg.footerHtml){
                                    var footerDiv=document.createElement('div');
                                    footerDiv.className='msg-footer';
                                    footerDiv.innerHTML=msg.footerHtml;
                                    container.appendChild(footerDiv);
                                }
                                // 推理面板
                                if(msg.reasoningHtml){
                                    var rp=document.getElementById('reasoning-'+msg.i);
                                    var rb=document.getElementById('reasoning-body-'+msg.i);
                                    if(rp&&rb){
                                        rp.style.display='block';
                                        var reasoningAtBottom=window.__isReasoningAtBottom(rb);
                                        rb.innerHTML=msg.reasoningHtml;
                                        if(reasoningAtBottom)window.__scrollReasoningToBottom(rb);
                                    }
                                }
                                // 高亮代码块
                                var msgDiv=document.getElementById('msg-'+msg.i);
                                if(msgDiv&&window.hljs)decorateCodeBlocks(msgDiv);
                                // 渲染数学公式（KaTeX）
                                window.__renderMath(container);
                                // 渲染 Mermaid 图表
                                window.__renderMermaid(container);
                                window.__wireSvgDiagramPreviews(container);
                            }
                        } catch(renderErr) {
                            // ── innerHTML 渲染失败时的降级：显示带样式的错误提示 ──
                            console.error('[DeepSeek] streamEnd render error:', renderErr);
                            if (containerEl) {
                                containerEl.innerHTML = '<div style=\'padding:12px;border-left:3px solid #e07878;background:#2a1a1a;color:#e07878;font-size:12px\'>' + (window.__markdownFailText||'') + '</div>' +
                                    '<pre style=\'max-height:300px;overflow-y:auto;font-size:11px;margin-top:8px\'>' +
                                    (msg.rawContent||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;') +
                                    '</pre>';
                            }
                        }
                    }
                    // 注入重试/复制按钮（无论渲染成功与否都尝试注入，失败时也能看到按钮）
                    try {
                        var msgDiv2=document.getElementById('msg-'+msg.i);
                        if(msgDiv2&&!document.getElementById('retry-btn-'+msg.i)){
                            var retryBtn=document.createElement('button');
                            retryBtn.id='retry-btn-'+msg.i;
                            retryBtn.className='msg-action-btn retry-btn';
                            retryBtn.textContent=msg.retryLabel||__actLabels.retry;
                            retryBtn.title=msg.retryTitle||__actLabels.retryTitle;
                            retryBtn.onclick=function(){window.__retryMessage(msg.i);};
                            var row=document.querySelector('#msg-'+msg.i+' .msg-actions-row');
                            if(!row){row=document.createElement('div');row.className='msg-actions-row';
                                var bubble=msgDiv2.querySelector('.msg-bubble')||msgDiv2;
                                bubble.appendChild(row);}
                            row.insertBefore(retryBtn,row.firstChild);
                        }
                        if(msgDiv2&&!document.getElementById('copy-btn-'+msg.i)){
                            var copyBtn=document.createElement('button');
                            copyBtn.id='copy-btn-'+msg.i;
                            copyBtn.className='msg-action-btn copy-msg-btn';
                            copyBtn.textContent=msg.copyBtn||__actLabels.copy;
                            copyBtn.title=msg.copyLabel||__actLabels.copyTitle;
                            copyBtn.setAttribute('data-copy-label',msg.copyBtn||__actLabels.copy);
                            copyBtn.setAttribute('data-copied-label',msg.copyFeedback||__actLabels.copied);
                            copyBtn.onclick=function(){window.__copyMessage(msg.i);};
                            var row2=document.querySelector('#msg-'+msg.i+' .msg-actions-row');
                            if(row2)row2.appendChild(copyBtn);
                            else{var mb=document.getElementById('msg-body-'+msg.i);
                                if(mb)mb.parentNode.insertBefore(copyBtn,mb.nextSibling);}
                        }
                    } catch(btnErr) {
                        console.error('[DeepSeek] streamEnd button injection error:', btnErr);
                    }
                    // 清除状态栏文本
                    var st = document.getElementById('status-text');
                    if (st) st.textContent = '';
                }else if(msg.type==='prependHtml'){
                    var cc2=document.getElementById('chat-container');
                    if(cc2){
                        cc2.insertAdjacentHTML('afterbegin',msg.html);
                        if(typeof window.__wireSvgDiagramPreviews==='function'){
                            window.__wireSvgDiagramPreviews(cc2);
                        }
                    }
                    var lb=document.getElementById('load-earlier-wrap');
                    if(lb&&!msg.hasMore){lb.parentNode.removeChild(lb);}
                }else if(msg.type==='appendHtml'){
                    // ── 增量追加 HTML（避免 NavigateToString 全量刷新导致滚动/闪烁）──
                    var temp=document.createElement('div');
                    temp.innerHTML=msg.html;
                    var fragment=document.createDocumentFragment();
                    while(temp.firstChild)fragment.appendChild(temp.firstChild);
                    var chatContainer=document.getElementById('chat-container');
                    if(chatContainer){
                        var taskPanel=chatContainer.querySelector('[id^="agent-task-panel-"]');
                        if(taskPanel)chatContainer.insertBefore(fragment,taskPanel);
                        else chatContainer.appendChild(fragment);
                        if(typeof window.__scrollToBottom==='function')window.__scrollToBottom('auto');
                    }
                }
            }catch(e) {
                console.error('[DeepSeek] Message handler error:', e);
            }
        });
    }

    function _flushStreamBuf(ts){
        var keys=Object.keys(_streamBuf);
        if(keys.length===0){_rafPending=false;return;}

        // 批量处理所有待更新的消息
        for(var i=0;i<keys.length;i++){
            var msg=_streamBuf[keys[i]];
            if(!msg)continue;

            var container=document.getElementById('msg-body-'+msg.i);
            var reasoningPanel=document.getElementById('reasoning-'+msg.i);
            var reasoningBody=document.getElementById('reasoning-body-'+msg.i);
            var cursor=document.getElementById('cursor-'+msg.i);

            // 更新正文内容
            if(container&&(msg.c!==undefined||msg.cd!==undefined)){
                // 防护：若 streamEnd 已将 _textNode 显式置为 null，说明已渲染完成，
                //    此时不应再创建 textNode 覆盖 innerHTML（防止 late chunk 竞态）
                if(container._textNode===null)continue;
                var textNode=container._textNode;
                if(!textNode){
                    textNode=document.createTextNode('');
                    container._textNode=textNode;
                    container.textContent='';
                    container.appendChild(textNode);
                    container.style.whiteSpace='pre-line';  // 流式内容保留换行
                }
                if(msg.c!==undefined){
                    // 内容未变时不重写文本节点：textContent 赋值会触发整块文本重排版。
                    // C# 侧已按「仅正文变化才带 c」裁剪，这里再兜一层幂等保护。
                    if(textNode.data!==msg.c)textNode.textContent=msg.c;
                }else if(msg.cd!==undefined)textNode.appendData(msg.cd);
            }

            // 更新推理面板。rd 是增量追加路径，避免长 thinking 反复替换整段 DOM。
            if(reasoningPanel&&reasoningBody&&(msg.r!==undefined||msg.rd!==undefined)){
                if(msg.r!==undefined&&msg.r.length>0){
                    reasoningPanel.style.display='block';
                    var reasoningAtBottom=window.__isReasoningAtBottom(reasoningBody);
                    reasoningBody._textNode=null;
                    reasoningBody.textContent=msg.r;
                    if(reasoningAtBottom)window.__scrollReasoningToBottom(reasoningBody);
                }else if(msg.rd!==undefined&&msg.rd.length>0&&!reasoningBody._streamComplete){
                    reasoningPanel.style.display='block';
                    var deltaAtBottom=window.__isReasoningAtBottom(reasoningBody);
                    var reasoningTextNode=reasoningBody._textNode;
                    if(!reasoningTextNode){
                        reasoningTextNode=document.createTextNode('');
                        reasoningBody._textNode=reasoningTextNode;
                        reasoningBody.textContent='';
                        reasoningBody.appendChild(reasoningTextNode);
                    }
                    reasoningTextNode.appendData(msg.rd);
                    if(deltaAtBottom)window.__scrollReasoningToBottom(reasoningBody);
                }
            }

            // 更新光标
            if(cursor&&msg.f!==undefined){
                cursor.style.display=msg.f?'none':'inline-block';
            }
        }

        _streamBuf={};
        _rafPending=false;
        if(_rafTimer){clearTimeout(_rafTimer);_rafTimer=null;}
        // 流式期间用 auto：高频刷新下反复发起 smooth 滚动会互相打断，
        // 在合成器里堆积动画，是长会话流式卡顿的常见来源。
        if (typeof window.__scrollToBottom === 'function') {
            window.__scrollToBottom('auto');
        }
    }
})();