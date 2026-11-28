
window.__insertBeforeTaskPanel=function(element){
    var container=document.getElementById('chat-container');
    if(!container)return;
    var taskPanel=container.querySelector('[id^="agent-task-panel-"]');
    if(taskPanel){
        container.insertBefore(element, taskPanel);
    } else {
        container.appendChild(element);
    }
};

// 任务面板折叠/展开：面板头部点击与头部右侧的向下箭头共用同一份逻辑
window.__toggleTaskPanel=function(planId){
    var panel=planId?document.getElementById('agent-task-panel-'+planId):null;
    if(!panel){
        var container=document.getElementById('chat-container');
        panel=container?container.querySelector('[id^="agent-task-panel-"]'):null;
    }
    if(!panel)return;
    panel.classList.toggle('collapsed');
    window.__syncTaskPanelArrow(panel);
};

// 同步箭头方向与提示文案：展开时显示向下箭头（点击向下收起），收起时显示向右箭头（点击展开）
window.__syncTaskPanelArrow=function(panel){
    if(!panel)return;
    var arrow=panel.querySelector('.task-collapse-arrow');
    if(!arrow)return;
    var collapsed=panel.classList.contains('collapsed');
    arrow.textContent=collapsed?'\u25B8':'\u25BE';
    arrow.setAttribute('aria-expanded',collapsed?'false':'true');
    var titleExpanded=arrow.getAttribute('data-title-expanded')||'';
    var titleCollapsed=arrow.getAttribute('data-title-collapsed')||'';
    arrow.title=collapsed?titleCollapsed:titleExpanded;
};

window.__appendMessageHtml=function(html){
    var container=document.getElementById('chat-container');
    if(!container)return;
    // 使用 DocumentFragment 批量追加，减少回流次数
    var temp=document.createElement('div');
    temp.innerHTML=html;
    var fragment=document.createDocumentFragment();
    while(temp.firstChild){
        fragment.appendChild(temp.firstChild);
    }
    // 如果存在 agent-task-panel，将新消息插入面板之前（保持面板始终在最底部）
    var taskPanel=container.querySelector('[id^="agent-task-panel-"]');
    if(taskPanel){
        container.insertBefore(fragment, taskPanel);
    } else {
        container.appendChild(fragment);
    }
    // 流式追加期间跳过语法高亮（节省性能），由 streamEnd 分支统一处理
    if (typeof window.__scrollToBottom === 'function') {
        window.__scrollToBottom('smooth');
    }
    if (typeof window.__wireSvgDiagramPreviews === 'function') {
        window.__wireSvgDiagramPreviews(container);
    }
    // 渲染数学公式（KaTeX）
    window.__renderMath(container);
    // 渲染 Mermaid 图表
    window.__renderMermaid(container);
};
