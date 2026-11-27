#include "Platform.h"
#include "Rtsp.h"

#include "piulink_rtsp.h"
int parseRtspMessage(PRTSP_MESSAGE msg, char *data, int len) {
    return pl_rtsp_parse(msg, data, len, malloc, free);
}
// Create new RTSP message struct with response data
void createRtspResponse(PRTSP_MESSAGE msg, char* message, int flags, char* protocol,
    int statusCode, char* statusString, int sequenceNumber, POPTION_ITEM optionsHead, char* payload, int payloadLength) {
    msg->type = TYPE_RESPONSE;
    msg->flags = flags;
    msg->messageBuffer = message;
    msg->protocol = protocol;
    msg->options = optionsHead;
    msg->payload = payload;
    msg->payloadLength = payloadLength;
    msg->sequenceNumber = sequenceNumber;
    msg->message.response.statusString = statusString;
    msg->message.response.statusCode = statusCode;
}

// Create new RTSP message struct with request data
void createRtspRequest(PRTSP_MESSAGE msg, char* message, int flags,
    char* command, char* target, char* protocol, int sequenceNumber, POPTION_ITEM optionsHead, char* payload, int payloadLength) {
    msg->type = TYPE_REQUEST;
    msg->flags = flags;
    msg->protocol = protocol;
    msg->messageBuffer = message;
    msg->options = optionsHead;
    msg->payload = payload;
    msg->payloadLength = payloadLength;
    msg->sequenceNumber = sequenceNumber;
    msg->message.request.command = command;
    msg->message.request.target = target;
}

// Retrieves option content from the linked list given the option title
char* getOptionContent(POPTION_ITEM optionsHead, char* option) {
    POPTION_ITEM current = optionsHead;
    while (current != NULL) {
        // Check if current node is what we're looking for
        if (!strcmp(current->option, option)) {
            return current->content;
        }
        current = current->next;
    }
    // Not found
    return NULL;
}

// Adds new option opt to the struct's option list
void insertOption(POPTION_ITEM* optionsHead, POPTION_ITEM opt) {
    POPTION_ITEM current = *optionsHead;
    opt->next = NULL;

    // Empty options list
    if (*optionsHead == NULL) {
        *optionsHead = opt;
        return;
    }

    // Traverse the list and insert the new option at the end
    while (current != NULL) {
        // Check for duplicate option; if so, replace the option currently there
        if (!strcmp(current->option, opt->option)) {
            current->content = opt->content;
            return;
        }
        if (current->next == NULL) {
            current->next = opt;
            return;
        }
        current = current->next;
    }
}

// Free every node in the message's option list
void freeOptionList(POPTION_ITEM optionsHead) {
    POPTION_ITEM current = optionsHead;
    POPTION_ITEM temp;
    while (current != NULL) {
        temp = current;
        current = current->next;
        if (temp->flags & FLAG_ALLOCATED_OPTION_FIELDS) {
            free(temp->option);
            free(temp->content);
        }
        free(temp);
    }
}

char *serializeRtspMessage(PRTSP_MESSAGE msg, int *len) {
    return pl_rtsp_serialize(msg, len, malloc);
}
// Free everything in a msg struct
void freeMessage(PRTSP_MESSAGE msg) {
    // If we've allocated the message buffer
    if (msg->flags & FLAG_ALLOCATED_MESSAGE_BUFFER) {
        free(msg->messageBuffer);
    }

    // If we've allocated any option items
    if (msg->flags & FLAG_ALLOCATED_OPTION_ITEMS) {
        freeOptionList(msg->options);
    }

    // If we've allocated the payload
    if (msg->flags & FLAG_ALLOCATED_PAYLOAD) {
        free(msg->payload);
    }
}
